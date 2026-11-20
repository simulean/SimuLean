// Standalone tests of the milk run (tugger train with tours, empties, departure rules): hand-computed tour
// times, link statistics, capacity rules, a destination that is full on arrival, a break of the member, network travel.
// Run: dotnet run  (from StandaloneTests/milkruntests)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean;
using SimuLean.Headless;
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
        => Check(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected.ToString(CultureInfo.InvariantCulture)}, got {actual.ToString(CultureInfo.InvariantCulture)}");

    static int Main()
    {
        Run("TaskSequence.Expand inserts steps where it is reached", TestExpand);
        Run("Basic tour: load in turn up to capacity, legs, unload per stop; waiting when nothing is ready", TestBasic);
        Run("The departure wait is its own state (WAITING_DEPARTURE): not busy, the member still reserved", TestDepartureWaitNotBusy);
        Run("Reference model milk_run_two_tours: hand-checked tours A, B, A, B, A, empties, stages, link statistics", TestReference);
        Run("Departure: Schedule (every 50) and All rule", TestScheduleAndAll);
        Run("Pattern on demand; capacity by carrier type", TestOnDemandAndTypes);
        Run("Departure rule per flow: a full load (min per tour, queued carriers counted) and optional flows", TestMinPerTourAndOptional);
        Run("A destination full on arrival keeps the train HELD until it has room", TestFullDestination);
        Run("Two milk runs into one destination count each other's promised room", TestTwoMilkRunsShareRoom);
        Run("A break of the member pauses the tour", TestBreak);
        Run("Spatial train: network travel between the stops' locations", TestNetwork);
        Run("Config errors fail loudly", TestErrors);
        Run("Dynamic shuttle, capacity 1, FIFO: pick-ups and deliveries on demand through a machine", TestShuttleCapacityOne);
        Run("Dynamic shuttle: time-out waits for a second carrier; fill-then-deliver visits origins until full", TestShuttlePolicies);
        Run("Assigned before the trip: delivery in loading order or nearest first; fill time-out (none, T, until full)", TestAssignedBeforeTrip);
        Run("Opportunistic: a carrier loaded for the stop it is at is unloaded before the next is loaded", TestOpportunisticSameStop);
        Run("Origin access Any: carriers behind a blocked head are requests; order by an origin property", TestOriginAccessAndProperty);
        Run("Statistics: load on board, distance, time in system at the sink", TestShuttleStatistics);
        Run("Named request orders: choices for the optimiser; reference model milk_run_shuttle_line", TestNamedOrders);
        Run("Urgency rule: origin fill / free, calculated request fields, multi-key order (nearest with priority to near-full origins)", TestUrgencyOrder);
        Run("Request fields cannot read other elements (content / free / get) nor origin.NAME; a machine's state is no expression value", TestRequestFieldsReach);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    // ── model helpers ───────────────────────────────────────────────────

    static ElementConfig El(string id, string type) => new ElementConfig(id, type, id);

    static ElementConfig Queue(string id, int cap) { var e = El(id, "Queue"); e.Set("capacity", cap); return e; }

    static ElementConfig Server(string id, double time)
    {
        var e = El(id, "MultiServer");
        e.Set("capacity", 1); e.Set("delayStrategy.strategyType", "Constant"); e.Set("delayStrategy.constantValue", time);
        return e;
    }

    static ElementConfig Batches(string id, string type, params (double time, int count)[] rows)
    {
        var table = new Dictionary<string, List<string>> { ["Time"] = new List<string>(), ["Name"] = new List<string>(), ["Q"] = new List<string>() };
        foreach (var r in rows)
        {
            table["Time"].Add(r.time.ToString(CultureInfo.InvariantCulture)); table["Name"].Add(type); table["Q"].Add(r.count.ToString(CultureInfo.InvariantCulture));
        }
        var e = El(id, "Source");
        e.Set("mode", "Schedule"); e.Set("arrivalsTable", table);
        return e;
    }

    /// <summary>4 A + 2 B at the depot; lineA (2) at S1, lineB (5) at S2, nothing consumed; one type, capacity 3;
    /// load 1, unload 2; legs D→S1 10, S1→S2 5, S2→D 12.</summary>
    static SimulationConfig Basic()
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = 100 };
        c.Elements.Add(Batches("srcA", "WagonA", (0, 4))); c.Elements.Add(Batches("srcB", "ToteB", (0, 2)));
        c.Elements.Add(Queue("orgA", 10)); c.Elements.Add(Queue("orgB", 10));
        c.Elements.Add(Queue("lineA", 2)); c.Elements.Add(Queue("lineB", 5));
        c.Connections.Add(new ConnectionConfig("srcA", "orgA", "General")); c.Connections.Add(new ConnectionConfig("srcB", "orgB", "General"));
        var t = new ResourceTeamConfig { Id = "tug", Name = "tug" };
        t.Members.Add(new ResourceMemberConfig { Name = "Tugger", LoadTime = 1, UnloadTime = 2 });
        c.ResourceTeams.Add(t);
        var mr = El("milk", "MilkRun");
        mr.Set("transportBy.team", "tug");
        mr.Set("capacity", 3);
        mr.Set("stops", new List<MilkRunStopSettings> { MilkRunStopSettings.Of("S1", "", null), MilkRunStopSettings.Of("S2", "", null) });
        mr.Set("flows", new List<MilkRunFlowSettings> { MilkRunFlowSettings.Of("fA", "orgA", "S1", "lineA"), MilkRunFlowSettings.Of("fB", "orgB", "S2", "lineB") });
        mr.Set("legs", new List<MilkRunLegSettings>
            { MilkRunLegSettings.Of("Depot", "S1", SamplerSettings.Constant(10)), MilkRunLegSettings.Of("S1", "S2", SamplerSettings.Constant(5)), MilkRunLegSettings.Of("S2", "Depot", SamplerSettings.Constant(12)) });
        c.Elements.Add(mr);
        return c;
    }

    sealed class Run1
    {
        public SimClock Clock; public HeadlessModelFactory Factory; public Dictionary<string, Element> El;
        public MilkRun Milk; public List<(double end, string type, double depart)> Tours = new List<(double, string, double)>();
        public TaskExecutor Member => Factory.Executors[0];
    }

    static Run1 Build(SimulationConfig c, Action<Run1> before = null)
    {
        var r = new Run1 { Clock = new SimClock(c.Seed) };
        r.Factory = new HeadlessModelFactory(r.Clock);
        r.El = r.Factory.BuildModel(c);
        r.Milk = (MilkRun)r.El["milk"];
        r.Milk.TourEnded += (m, t) => r.Tours.Add((r.Clock.GetSimulationTime(), t.TourType?.Name ?? "dynamic", t.DepartTime));
        r.Factory.StartModel();
        before?.Invoke(r);
        r.Clock.AdvanceClock(c.MaxSimulationTime);
        return r;
    }

    static string Ends(Run1 r) => string.Join(" ", r.Tours.Select(t => t.end.ToString(CultureInfo.InvariantCulture)));

    sealed class At : IEvent { readonly Action a; public At(Action a) { this.a = a; } public void Execute() => a(); }

    // ── tests ───────────────────────────────────────────────────────────

    static void TestExpand()
    {
        var clock = new SimClock(1);
        var ex = new TaskExecutor("X", clock);
        ex.Start();
        var log = new List<string>();
        int n = 0;
        var task = new TaskSequence(TaskType.Other);
        task.Process(1)
            .Expand(t => { t.Callback(() => log.Add("a@" + clock.GetSimulationTime())).Process(2); })
            .Expand(t => { if (n++ < 2) t.Process(1).Callback(() => log.Add("loop@" + clock.GetSimulationTime())); })
            .Callback(() => log.Add("end@" + clock.GetSimulationTime()));
        ex.Execute(task);
        clock.AdvanceClock(100);
        Check(string.Join(" ", log) == "a@1 loop@4 end@4", string.Join(" ", log));
        Check(task.IsDone && ex.TasksDone == 1, "task completed");
        bool threw = false;
        try { task.Process(1); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "no steps added after the start outside an expansion");
    }

    static void TestBasic()
    {
        var r = Build(Basic());
        // tour 1: load A1, B1, A2 (0–3; lineA has room for 2), D→S1 3–13, unload A1, A2 13–17, S1→S2 17–22, unload B1 22–24,
        // S2→D 24–36. Tour 2 at 36: only B2 (lineA full): load 36–37, →S1 47, →S2 52, unload 52–54, →D 66. Then nothing is ready.
        Check(Ends(r) == "36 66", "tour ends " + Ends(r));
        var m = r.Milk;
        Check(m.Tours == 2 && m.Delivered == 4 && m.DeliveredByFlow["fA"] == 2 && m.DeliveredByFlow["fB"] == 2, "deliveries");
        Near(m.TourTime.Average, 33, "tour time avg"); Near(m.TourTime.Max, 36, "tour time max");
        Near(m.DepotStage.Average, 2, "depot stage (3, 1)"); Near(m.OutboundStage.Average, (21 + 17) / 2.0, "outbound stage");
        Near(m.ReturnStage.Average, 12, "return stage"); Near(m.EmptiesStage.Average, 0, "no empties");
        Near(m.DepartureWait.Average, 0, "no waiting");
        Check(r.El["orgA"].GetStatsOutputCount() == 2 && r.El["lineA"].GetStatsInputCount() == 2, "link statistics: orgA out 2, lineA in 2");
        Check(r.El["orgA"].GetQueueLength() == 2 && r.El["orgB"].GetQueueLength() == 0, "two A left at the depot");
        // main's queue rule: items waiting for a puller (a list, or the milk run that serves the queue) are not
        // blocked — the queue holds, PROCESSING
        Check(r.El["orgA"].CurrentState == ElementState.PROCESSING, "origin holding (not BLOCKED) while its carriers wait for the train");
        Check(m.CurrentState == MilkRun.WAITING_DEPARTURE, "milk run waiting for departure at the end");
        var ex = r.Member;
        Near(ex.GetTimeInState(ResourceStates.WAITING_DEPARTURE), 34, "member WAITING_DEPARTURE at the depot 66–100");
        Near(ex.GetTimeInState(ResourceStates.WAITING), 0, "no busy WAITING");
        Near(ex.GetTimeInState(ResourceStates.LOADING), 4, "loading 4 carriers");
        Near(ex.GetTimeInState(ResourceStates.UNLOADING), 8, "unloading 4 carriers");
        Check(ex.TasksDone == 2, "one task per tour");
    }

    // the train waiting at the depot for its departure rule keeps its member (the tour
    // task) but the wait is not utilisation.
    static void TestDepartureWaitNotBusy()
    {
        var r = Build(Basic());   // tours end at 36 and 66, then nothing is ready: waits 66–100
        var ex = r.Member;
        Check(ex.CurrentState == ResourceStates.WAITING_DEPARTURE, $"member waiting for departure at the end ({ex.CurrentState.Name})");
        Check(!ResourceStates.Busy.Contains(ResourceStates.WAITING_DEPARTURE), "WAITING_DEPARTURE is not a busy state");
        Check(ex.CurrentTask != null && !ex.IsAvailable, "the member keeps its tour task: not available");
        Near(ex.Utilization, (100 - 34) / 100.0, "utilisation leaves out the departure wait");
        // another requester of the same team gets nothing while the train waits
        var team = r.Factory.Teams.Values.Single();
        bool granted = false;
        var other = new ResourceRequest(r.El["lineA"], TaskType.Transport, (q, g) => granted = true);
        team.Request(other);
        r.Clock.AdvanceClock(150);
        Check(!granted && other.State == RequestState.Pending, $"a second request stays pending ({other.State})");
        Check(ex.CurrentTask != null && ex.CurrentState == ResourceStates.WAITING_DEPARTURE, "still reserved for the train");
        // results: through RunResults / ResultMetrics
        var res = RunResults.Capture(r.Factory, "milk");
        var o = res.Find(ResultScope.Resource, ex.Name) ?? res.Objects.FirstOrDefault(x => x.Scope == ResultScope.Resource);
        Check(o != null && o.TryGet(ResultMetrics.State("WAITING_DEPARTURE"), out double w) && Math.Abs(w - 84 / 150.0) < 1e-9,
              "RunResults: state:WAITING_DEPARTURE = 84 / 150");
        Check(o != null && o.TryGet(ResultMetrics.Utilization, out double u) && Math.Abs(u - 66 / 150.0) < 1e-9, "RunResults: utilization = 66 / 150");
        Check(o != null && !o.TryGet(ResultMetrics.State("WAITING"), out _), "RunResults: no state:WAITING");
        Check(ElementState.IsKnown("WAITING_DEPARTURE"), "a known state name for studies");
    }

    static void TestReference()
    {
        var r = Build(ReferenceModels.MilkRunTwoTours());
        Check(Ends(r) == "41 77 122 186 227", "tour ends " + Ends(r));
        Check(string.Join("", r.Tours.Select(t => t.type)) == "ABABA", "types " + string.Join("", r.Tours.Select(t => t.type)));
        Check(string.Join(" ", r.Tours.Select(t => t.depart.ToString(CultureInfo.InvariantCulture))) == "4 44 80 153 188",
              "departures " + string.Join(" ", r.Tours.Select(t => t.depart.ToString(CultureInfo.InvariantCulture))));
        var m = r.Milk;
        Check(m.DeliveredByFlow["fA"] == 6 && m.DeliveredByFlow["fB"] == 9, $"delivered fA {m.DeliveredByFlow["fA"]} fB {m.DeliveredByFlow["fB"]}");
        Check(m.EmptiesReturned == 12, "empties returned " + m.EmptiesReturned);
        Check(r.El["emptyDock"].GetStatsInputCount() == 12, "empties enter the dock through the link");
        Check(r.El["mtA"].GetStatsOutputCount() == 4 && r.El["mtB"].GetStatsOutputCount() == 8, "empties leave their queues through the link");
        Near(m.DepartureWait.Average * m.DepartureWait.Count, 28, "waited 122 → 150 for the B carriers");
        // stages: depot (4, 3, 3, 31, 2), outbound (25, 19, 26, 19, 23), return 12, empties (0, 2, 4, 2, 4)
        Near(m.DepotStage.Average, 43 / 5.0, "depot stage avg");
        Near(m.OutboundStage.Average, 112 / 5.0, "outbound stage avg");
        Near(m.ReturnStage.Average, 12, "return stage avg");
        Near(m.EmptiesStage.Average, 12 / 5.0, "empties stage avg");
        Near(m.TourTime.Max, 64, "longest tour (B, with the wait)");
        Check(m.ToursByType["A"] == 3 && m.ToursByType["B"] == 2, "tours by type");
        Near(r.Member.GetTimeInState(ResourceStates.WAITING_DEPARTURE), 28 + 73, "member waits for departure 122–150 and 227–300");

        var res = RunResults.Capture(r.Factory, "milk");
        var o = res.Find(ResultScope.Element, "milk");
        Check(o != null && o.TryGet(ResultMetrics.Tours, out double tours) && tours == 5, "RunResults: tours");
        Check(o != null && o.TryGet(ResultMetrics.ToursOf("B"), out double b) && b == 2, "RunResults: tours:B");
        Check(o != null && o.TryGet(ResultMetrics.DeliveredBy("fB"), out double fb) && fb == 9, "RunResults: delivered:fB");
        Check(o != null && o.TryGet(ResultMetrics.DepartureWaitTotal, out double w) && Math.Abs(w - 28) < 1e-9, "RunResults: departure_wait_total");
    }

    static void TestScheduleAndAll()
    {
        var c = Basic();
        var mr = c.Elements.Find(e => e.Id == "milk");
        mr.Set("departure", "Schedule");
        mr.Set("departureInterval", 50.0);
        mr.Set("firstDeparture", 0.0);
        c.MaxSimulationTime = 130;
        var r = Build(c);
        // 0: as the basic tour (36); 50: B2 (51 → S1 61 → S2 66, unload 66–68 → 80); 100: nothing, the train goes round (127).
        Check(Ends(r) == "36 80 127", "scheduled tour ends " + Ends(r));
        Near(r.Milk.DepartureWait.Average * r.Milk.DepartureWait.Count, 0 + 14 + 20, "waits for the timetable");

        var c2 = Basic();
        c2.Elements.Find(e => e.Id == "milk").Set("readyRule", "All");
        var r2 = Build(c2);
        Check(Ends(r2) == "36", "All: the second tour waits while lineA is full (" + Ends(r2) + ")");

        // All + ReadyMaxWait 20: back at 36, lineA is full, so All is not met; at 56 the train leaves with what is ready (the B).
        var c3 = Basic();
        var m3 = c3.Elements.Find(e => e.Id == "milk");
        m3.Set("readyRule", "All");
        m3.Set("readyMaxWait", 20.0);
        var r3 = Build(c3);
        Check(r3.Tours.Count == 2, "max wait: a second tour (" + Ends(r3) + ")");
        if (r3.Tours.Count == 2) Near(r3.Tours[1].depart, 57, "it leaves after waiting 20 (loads the B at 56–57)");
    }

    static void TestOnDemandAndTypes()
    {
        var c = ReferenceModels.MilkRunTwoTours();
        var mr = c.Elements.Find(e => e.Id == "milk");
        mr.Set("patternMode", "OnDemand");
        mr.Set("pattern", new List<string> { "B", "A" });
        c.MaxSimulationTime = 100;
        var r = Build(c);
        Check(r.Tours.Count >= 2 && r.Tours[0].type == "B" && r.Tours[1].type == "A", "on demand: B then A (" + string.Join("", r.Tours.Select(t => t.type)) + ")");

        var c2 = Basic();
        var m2 = c2.Elements.Find(e => e.Id == "milk");
        m2.Set("capacity", 0);
        m2.Set("capacityByType", new List<MilkRunTypeCapacitySettings> { new MilkRunTypeCapacitySettings { Type = "WagonA", Capacity = 1 } });
        c2.MaxSimulationTime = 70;
        int deliveredA = -1, deliveredB = -1;
        var r2 = Build(c2, run => run.Clock.ScheduleEvent(new At(() => { deliveredA = run.Milk.DeliveredByFlow["fA"]; deliveredB = run.Milk.DeliveredByFlow["fB"]; }), 36.5));
        // tour 1: A1, B1, B2 (one wagon at most) 0–3, S1 13, unload A1 13–15, S2 20, B1 B2 20–24, back 36;
        // tour 2: A2 36–37, S1 47 unload 47–49, S2 54, back 66.
        Check(Ends(r2) == "36 66", "type capacity tours " + Ends(r2));
        Check(deliveredA == 1 && deliveredB == 2, $"first tour: 1 wagon, 2 totes ({deliveredA}, {deliveredB})");
    }

    static void TestMinPerTourAndOptional()
    {
        // A1 at 0, A2 at 20 (queued behind A1 at orgA); fA needs 2 (lineA has room for 2), fB is optional: no tour at 0 although
        // A1 and both B are ready. At 20: load A1 20–21, B1 21–22, A2 22–23 (capacity 3), S1 33, unload A1 A2 33–37, S2 42,
        // unload B1 42–44, back 56. Then fA has none and fB alone (optional) does not make the tour ready.
        SimulationConfig Model(int lineA)
        {
            var c = Basic();
            c.Elements.RemoveAll(e => e.Id == "srcA");
            c.Elements.Add(Batches("srcA", "WagonA", (0, 1), (20, 1)));
            c.Elements.Find(e => e.Id == "lineA").Set("capacity", lineA);
            c.Elements.Find(e => e.Id == "milk").Set("flowReadiness", new List<MilkRunReadinessSettings> { MilkRunReadinessSettings.Of("fA", 2, false), MilkRunReadinessSettings.Of("fB", 0, true) });
            return c;
        }
        var r = Build(Model(2));
        Check(Ends(r) == "56", "one full tour (" + Ends(r) + ")");
        if (r.Tours.Count > 0) Near(r.Tours[0].depart, 23, "it waits for the second A (20), loads three, leaves at 23");
        Check(r.Milk.DeliveredByFlow["fA"] == 2 && r.Milk.DeliveredByFlow["fB"] == 1, $"delivered fA {r.Milk.DeliveredByFlow["fA"]} fB {r.Milk.DeliveredByFlow["fB"]}");
        Near(r.Milk.DepartureWait.Average * r.Milk.DepartureWait.Count, 20, "waited 0 → 20 for a full load");

        // room for one A only: a full load (2) never fits, so no tour leaves (the optional B do not make it ready)
        var r2 = Build(Model(1));
        Check(r2.Tours.Count == 0 && r2.Milk.Delivered == 0, "no room for a full load: no tour (" + Ends(r2) + ")");
    }

    static void TestFullDestination()
    {
        var c = Basic();
        c.Elements.Find(e => e.Id == "lineA").Set("capacity", 1);
        c.Elements.Add(Server("useA", 30));
        c.Elements.Add(Batches("intruder", "Other", (5, 2)));
        c.Elements.Add(El("sink", "Sink"));
        c.Connections.Add(new ConnectionConfig("lineA", "useA", "General"));
        c.Connections.Add(new ConnectionConfig("useA", "sink", "General"));
        c.Connections.Add(new ConnectionConfig("intruder", "lineA", "General"));
        c.MaxSimulationTime = 60;
        var r = Build(c);
        // load A1 (room 1), B1, B2 0–3; S1 at 13, unload 13–15: lineA is full (the intruders: one in useA 5–35, one waiting)
        // → HELD until 35; S2 at 40, unload 40–44, back at 56.
        Check(r.Tours.Count == 1 && r.Tours[0].end == 56, "tour end " + Ends(r));
        Near(r.Member.GetTimeInState(ResourceStates.HELD), 20, "HELD 15–35 at the full destination");
        Check(r.El["lineA"].GetStatsInputCount() == 3, "lineA received the intruders and A1");
    }

    static void TestTwoMilkRunsShareRoom()
    {
        // A second milk run also delivers to lineA (room 2). Each counts the places the other promised: lineA gets two
        // carriers in all, and no train waits HELD at it (counted per milk run, each promised both places: two trains
        // brought four carriers and one waited at the full line for ever).
        var c = Basic();
        c.Elements.Add(Batches("srcC", "WagonC", (0, 2)));
        c.Elements.Add(Queue("orgC", 10));
        c.Connections.Add(new ConnectionConfig("srcC", "orgC", "General"));
        var t = new ResourceTeamConfig { Id = "tug2", Name = "tug2" };
        t.Members.Add(new ResourceMemberConfig { Name = "Tugger2", LoadTime = 1, UnloadTime = 2 });
        c.ResourceTeams.Add(t);
        var mr = El("milk2", "MilkRun");
        mr.Set("transportBy.team", "tug2");
        mr.Set("capacity", 3);
        mr.Set("stops", new List<MilkRunStopSettings> { MilkRunStopSettings.Of("S1", "", null) });
        mr.Set("flows", new List<MilkRunFlowSettings> { MilkRunFlowSettings.Of("fC", "orgC", "S1", "lineA") });
        mr.Set("legs", new List<MilkRunLegSettings> { MilkRunLegSettings.Of("Depot", "S1", SamplerSettings.Constant(10)), MilkRunLegSettings.Of("S1", "Depot", SamplerSettings.Constant(10)) });
        c.Elements.Add(mr);
        var r = Build(c);
        var lineA = r.El["lineA"];
        var second = (MilkRun)r.El["milk2"];
        Check(lineA.GetStatsInputCount() == 2 && lineA.GetQueueLength() == 2, $"lineA got two carriers in all ({lineA.GetStatsInputCount()})");
        int a = r.Milk.DeliveredByFlow.TryGetValue("fA", out int na) ? na : 0, cc = second.DeliveredByFlow.TryGetValue("fC", out int nc) ? nc : 0;
        Check(a + cc == 2, $"the two milk runs shared its two places (fA {a}, fC {cc})");
        Check(r.Factory.Executors.All(ex => ex.GetTimeInState(ResourceStates.HELD) == 0), "no train waited at the full line");
        Check(lineA.RoomPromised == 0, $"nothing promised at the end ({lineA.RoomPromised})");
    }

    static void TestBreak()
    {
        var r = Build(Basic(), run => run.Clock.ScheduleEvent(new At(() =>
        {
            var token = run.Member.Stop(new StopRequest(ElementState.BREAKDOWN));
            run.Clock.ScheduleEvent(new At(() => run.Member.Resume(token)), 10);
        }), 5));
        Check(r.Tours.Count >= 1 && r.Tours[0].end == 46, "a 10-unit breakdown mid-leg delays the tour (" + Ends(r) + ")");
        Near(r.Member.GetTimeInState(ElementState.BREAKDOWN), 10, "breakdown time");
    }

    static void TestNetwork()
    {
        var c = Basic();
        c.Network = new NetworkConfig();
        foreach (var n in new[] { ("D", 0.0, 0.0), ("P1", 10.0, 0.0), ("P2", 10.0, 5.0) })
            c.Network.Nodes.Add(NetworkNodeConfig.Of(n.Item1, n.Item2, 0, n.Item3));
        c.Network.Tracks.Add(NetworkTrackConfig.Of("D", "P1", 10, true, SimuLean.Transport.TrackType.TravelPath));
        c.Network.Tracks.Add(NetworkTrackConfig.Of("P1", "P2", 5, true, SimuLean.Transport.TrackType.TravelPath));
        c.Network.Tracks.Add(NetworkTrackConfig.Of("P2", "D", -1, true, SimuLean.Transport.TrackType.TravelPath));
        foreach (var (id, node) in new[] { ("orgA", "D"), ("orgB", "D"), ("lineA", "P1"), ("lineB", "P2") })
            c.Elements.Find(e => e.Id == id).Set("location", node);
        var t = c.ResourceTeams[0];
        t.Travel = TravelKind.Network; t.Speed = 1; t.HomeNode = "D";
        c.Elements.Find(e => e.Id == "milk").Remove("legs");
        c.MaxSimulationTime = 40;
        var r = Build(c);
        double back = Math.Sqrt(125);   // P2 → D straight (auto length)
        Check(r.Tours.Count == 1, "one tour");
        if (r.Tours.Count == 1) Near(r.Tours[0].end, 3 + 10 + 4 + 5 + 2 + back, "network tour end", 1e-6);
        Check(r.Milk.Depot.Location == "D" && r.Milk.Stop("S2").Location == "P2", "locations taken from the elements");
        Check(r.Member.Location == "D", "back at the depot");
    }

    // ── dynamic tours (rail shuttle) ────────────────────────────────────

    /// <summary>Stops In (the depot), M, Out on a line (legs 10, 10, 20 both ways); inQ at In → machine feed at M; the machine's
    /// output queue at M → outQ at Out. Load / unload 1.</summary>
    static SimulationConfig Shuttle(int capacity, int items, params (double time, int count)[] extra)
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 3, MaxSimulationTime = 300 };
        var rows = new List<(double, int)> { (0, items) };
        rows.AddRange(extra);
        c.Elements.Add(Batches("src", "Part", rows.ToArray()));
        c.Elements.Add(Queue("inQ", 10)); c.Elements.Add(Queue("mFeed", 2)); c.Elements.Add(Server("mach", 5));
        c.Elements.Add(Queue("mOut", 10)); c.Elements.Add(Queue("outQ", 10));
        c.Connections.Add(new ConnectionConfig("src", "inQ", "General"));
        c.Connections.Add(new ConnectionConfig("mFeed", "mach", "General")); c.Connections.Add(new ConnectionConfig("mach", "mOut", "General"));
        var t = new ResourceTeamConfig { Id = "rail", Name = "rail" };
        t.Members.Add(new ResourceMemberConfig { Name = "Shuttle", LoadTime = 1, UnloadTime = 1 });
        c.ResourceTeams.Add(t);
        var mr = El("milk", "MilkRun");
        mr.Set("transportBy.team", "rail");
        mr.Set("tourMode", "Dynamic");
        mr.Set("capacity", capacity);
        mr.Set("depotName", "In");
        mr.Set("stops", new List<MilkRunStopSettings> { MilkRunStopSettings.Of("M", "", null), MilkRunStopSettings.Of("Out", "", null) });
        mr.Set("pickups", new List<MilkRunPickupSettings> { MilkRunPickupSettings.Of("In", "inQ"), MilkRunPickupSettings.Of("M", "mOut") });
        mr.Set("flows", new List<MilkRunFlowSettings> { MilkRunFlowSettings.Of("toMachine", "inQ", "M", "mFeed"), MilkRunFlowSettings.Of("toOut", "mOut", "Out", "outQ") });
        mr.Set("symmetricLegs", true);
        mr.Set("legs", new List<MilkRunLegSettings>
            { MilkRunLegSettings.Of("In", "M", SamplerSettings.Constant(10)), MilkRunLegSettings.Of("M", "Out", SamplerSettings.Constant(10)), MilkRunLegSettings.Of("In", "Out", SamplerSettings.Constant(20)) });
        c.Elements.Add(mr);
        return c;
    }

    static void TestShuttleCapacityOne()
    {
        var times = new List<double>();
        var r = Build(Shuttle(1, 3), run => run.El["outQ"].ItemEntered += (e, it) => times.Add(run.Clock.GetSimulationTime()));
        // Hand trace (oldest request first, a full shuttle only delivers): I1 0–1 → M 11, unload 11–12 (machine 12–17);
        // I2 at In 22–23 → M 33–34 (machine 34–39), picks I1 34–35 → Out 45, unload 45–46; I3 at In (66) 66–67 → M 77–78
        // (machine 78–83), picks I2 78–79 → Out 89–90; picks I3 at M 100–101 → Out 111–112; nothing left: the trip ends.
        Check(string.Join(" ", times) == "46 90 112", "deliveries at Out " + string.Join(" ", times));
        Check(r.Tours.Count == 1 && r.Tours[0].end == 112, "one trip, ends at 112 (" + Ends(r) + ")");
        Check(r.El["inQ"].GetStatsOutputCount() == 3 && r.El["mFeed"].GetStatsInputCount() == 3 && r.El["mOut"].GetStatsOutputCount() == 3,
              "link statistics through the machine");
        var m = r.Milk;
        Check(m.Requests.Count == 0 && m.Requests.Pulled == 6, $"requests: 6 pick-ups served, none open ({m.Requests.Pulled}, {m.Requests.Count})");
        Check(m.CurrentState == MilkRun.WAITING_DEPARTURE && r.Member.CurrentState == ResourceStates.WAITING_DEPARTURE, "idle at the end, waiting for requests");
    }

    static void TestShuttlePolicies()
    {
        // Time-out: capacity 2, one part at 0 and one at 3 at In, nothing goes through the machine queue limit.
        var c = Shuttle(2, 1, (3, 1));
        var mr = c.Elements.Find(e => e.Id == "milk");
        mr.Set("loadTimeout", 5.0);
        var times = new List<double>();
        var r = Build(c, run => run.El["mFeed"].ItemEntered += (e, it) => times.Add(run.Clock.GetSimulationTime()));
        // load P1 0–1, wait (up to 5) for P2: offered at 3, loaded 3–4 (full) → M 14, unload 14–15, 15–16.
        Check(times.Count == 2 && times[0] == 15 && times[1] == 16, "time-out: both delivered together (" + string.Join(" ", times) + ")");

        var c2 = Shuttle(2, 1, (3, 1));
        var times2 = new List<double>();
        Build(c2, run => run.El["mFeed"].ItemEntered += (e, it) => times2.Add(run.Clock.GetSimulationTime()));
        // without: P1 0–1 → M 11–12; back to In 22, P2 22–23 → M 33–34.
        Check(times2.Count == 2 && times2[0] == 12 && times2[1] == 34, "no time-out: one at a time (" + string.Join(" ", times2) + ")");

        // Fill then deliver: capacity 2, a part at In and a finished part already at M (fed straight into mOut).
        var c3 = Shuttle(2, 1);
        c3.Elements.Add(Batches("done", "Part", (0, 1)));
        c3.Connections.Add(new ConnectionConfig("done", "mOut", "General"));
        c3.Elements.Find(e => e.Id == "milk").Set("policy", "FillThenDeliver");
        c3.Elements.Find(e => e.Id == "milk").Set("requestOrder", "|distance");
        var events = new List<string>();
        var r3 = Build(c3, run =>
        {
            run.El["mFeed"].ItemEntered += (e, it) => events.Add("feed@" + run.Clock.GetSimulationTime());
            run.El["outQ"].ItemEntered += (e, it) => events.Add("out@" + run.Clock.GetSimulationTime());
        });
        // P1 at In 0–1; not full and a pick-up waits at M → M 11, picks the finished part 11–12 (full) → deliver: nearest is M
        // (P1, unload 12–13), then Out 23–24; then P1 comes out of the machine at 18 → M ... later trips.
        Check(events.Count >= 2 && events[0] == "feed@13" && events[1] == "out@24", "fill then deliver: " + string.Join(" ", events));
    }

    // ── assignment before the trip, delivery orders, fill time-out, free access ─────────

    /// <summary>Stops A (the depot), B, C on a line (legs 10, 10, 20 both ways, constant: also their distances); origin qA at
    /// A; sinks sB at B and sC at C (system time on); label d = 1 → B, 2 → C. Load / unload 1. Rows: (time, d).</summary>
    static SimulationConfig Line(int capacity, params (double time, int d)[] rows)
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 5, MaxSimulationTime = 120 };
        var table = new Dictionary<string, List<string>> { ["Time"] = new List<string>(), ["Name"] = new List<string>(), ["Q"] = new List<string>(), ["d"] = new List<string>() };
        foreach (var r in rows)
        {
            table["Time"].Add(r.time.ToString(CultureInfo.InvariantCulture)); table["Name"].Add("Box"); table["Q"].Add("1");
            table["d"].Add(r.d.ToString(CultureInfo.InvariantCulture));
        }
        var src = El("src", "Source");
        src.Set("mode", "Schedule"); src.Set("arrivalsTable", table);
        c.Elements.Add(src);
        c.Elements.Add(Queue("qA", 10));
        foreach (var s in new[] { "sB", "sC" }) { var e = El(s, "Sink"); e.Set("tallies", new List<string> { "time_in_system = age" }); c.Elements.Add(e); }
        c.Connections.Add(new ConnectionConfig("src", "qA", "General"));
        var t = new ResourceTeamConfig { Id = "rail", Name = "rail" };
        t.Members.Add(new ResourceMemberConfig { Name = "Shuttle", LoadTime = 1, UnloadTime = 1 });
        c.ResourceTeams.Add(t);
        var mr = El("milk", "MilkRun");
        mr.Set("transportBy.team", "rail");
        mr.Set("tourMode", "Dynamic");
        mr.Set("capacity", capacity);
        mr.Set("depotName", "A");
        mr.Set("originAccess", "Any");
        mr.Set("stops", new List<MilkRunStopSettings> { MilkRunStopSettings.Of("B", "", null), MilkRunStopSettings.Of("C", "", null) });
        mr.Set("pickups", new List<MilkRunPickupSettings> { MilkRunPickupSettings.Of("A", "qA") });
        mr.Set("flows", new List<MilkRunFlowSettings>
            { MilkRunFlowSettings.Of("toB", "qA", "B", "sB", 0, null, null, "item.d == 1"), MilkRunFlowSettings.Of("toC", "qA", "C", "sC", 0, null, null, "item.d == 2") });
        mr.Set("symmetricLegs", true);
        mr.Set("legs", new List<MilkRunLegSettings>
            { MilkRunLegSettings.Of("A", "B", SamplerSettings.Constant(10)), MilkRunLegSettings.Of("B", "C", SamplerSettings.Constant(10)), MilkRunLegSettings.Of("A", "C", SamplerSettings.Constant(20)) });
        mr.Set("namedOrders", new List<NamedOrderSettings> { NamedOrderSettings.Of("Loading", "|pushTime"), NamedOrderSettings.Of("Nearest", "|distance") });
        c.Elements.Add(mr);
        return c;
    }

    static SimulationConfig FillLine(string deliveryOrder, double fillTimeout, params (double time, int d)[] rows)
    {
        var c = Line(2, rows);
        var p = c.Elements.Find(e => e.Id == "milk");
        p.Set("policy", "FillThenDeliver");
        p.Set("assignment", "BeforeTrip");
        p.Set("fillTimeout", fillTimeout);
        p.Set("deliveryOrder", deliveryOrder);
        return c;
    }

    static List<string> Arrivals(Run1 run, params string[] sinks)
    {
        var log = new List<string>();
        foreach (var s in sinks) { string n = s; run.El[s].ItemEntered += (e, it) => log.Add(n + "@" + run.Clock.GetSimulationTime().ToString(CultureInfo.InvariantCulture)); }
        return log;
    }

    static void TestAssignedBeforeTrip()
    {
        List<string> log = null;
        // P1 (loading order): X (→ C) loaded 0–1, Y (→ B) 1–2; full: X first: A → C 2–22, unload 22–23; C → B 23–33, unload 33–34.
        Build(FillLine("Loading", -1, (0, 2), (0, 1)), run => log = Arrivals(run, "sB", "sC"));
        Check(string.Join(" ", log) == "sC@23 sB@34", "loading order: " + string.Join(" ", log));
        // P2 (nearest destination): B first: A → B 2–12, unload 12–13; B → C 13–23, unload 23–24.
        Build(FillLine("Nearest", -1, (0, 2), (0, 1)), run => log = Arrivals(run, "sB", "sC"));
        Check(string.Join(" ", log) == "sB@13 sC@24", "nearest first: " + string.Join(" ", log));

        // Until full (−1): X loaded 0–1, waits for Y (offered at 50), loads it 50–51, delivers X: A → C 51–71, unload 71–72; B 82–83.
        var r = Build(FillLine("Loading", -1, (0, 2), (50, 1)), run => log = Arrivals(run, "sB", "sC"));
        Check(string.Join(" ", log) == "sC@72 sB@83", "fill until full: " + string.Join(" ", log));
        Near(r.Member.GetTimeInState(ResourceStates.WAITING), 49, "waits 1–50 with X on board (busy)");
        Near(r.Member.GetTimeInState(ResourceStates.WAITING_DEPARTURE), 120 - 83, "then idle, waiting for a request (not busy)");
        // Time-out 10: X 0–1, delivers at 11: A → C 11–31, unload 31–32. Y at 50: C → A 50–70, load 70–71, waits until 81,
        // A → B 81–91, unload 91–92.
        Build(FillLine("Loading", 10, (0, 2), (50, 1)), run => log = Arrivals(run, "sB", "sC"));
        Check(string.Join(" ", log) == "sC@32 sB@92", "fill time-out 10: " + string.Join(" ", log));
        // No time-out (0, the default): X at once: A → C 1–21, unload 21–22; Y: C → A 50–70, load 70–71, A → B 71–81, 81–82.
        Build(FillLine("Loading", 0, (0, 2), (50, 1)), run => log = Arrivals(run, "sB", "sC"));
        Check(string.Join(" ", log) == "sC@22 sB@82", "no time-out: " + string.Join(" ", log));
    }

    static void TestOpportunisticSameStop()
    {
        // Shuttle (In, M, Out): two parts already in the machine's output queue at M, both for Out — and a third origin queue
        // at M whose carriers go to the machine feed at the same stop. Capacity 2; the train starts at In (empty).
        var c = Shuttle(2, 0);
        c.Elements.Find(e => e.Id == "src").Set("arrivalsTable", new Dictionary<string, List<string>>
            { ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "Part" }, ["Q"] = new List<string> { "1" } });
        c.Connections.RemoveAll(x => x.SourceId == "src");
        c.Elements.Add(Queue("mIn", 5));
        c.Connections.Add(new ConnectionConfig("src", "mIn", "General"));
        c.Elements.Add(Batches("done", "Part", (0, 1)));
        c.Connections.Add(new ConnectionConfig("done", "mOut", "General"));
        var p = c.Elements.Find(e => e.Id == "milk");
        p.Set("pickups", new List<MilkRunPickupSettings> { MilkRunPickupSettings.Of("M", "mIn", "mOut") });
        p.Set("flows", new List<MilkRunFlowSettings> { MilkRunFlowSettings.Of("feed", "mIn", "M", "mFeed"), MilkRunFlowSettings.Of("toOut", "mOut", "Out", "outQ") });
        p.Set("originAccess", "Any");
        c.Elements.Find(e => e.Id == "mach").Set("delayStrategy.constantValue", 1000.0);   // keeps the fed part
        var log = new List<string>();
        Build(c, run =>
        {
            run.El["mFeed"].ItemEntered += (e, it) => log.Add("feed@" + run.Clock.GetSimulationTime());
            run.El["outQ"].ItemEntered += (e, it) => log.Add("out@" + run.Clock.GetSimulationTime());
        });
        // In → M 0–10; load the mIn part 10–11 (for this very stop), unload it 11–12; load the mOut part 12–13; M → Out 13–23,
        // unload 23–24.
        Check(string.Join(" ", log) == "feed@12 out@24", "opportunistic at one stop: " + string.Join(" ", log));
    }

    static void TestOriginAccessAndProperty()
    {
        // A blocked head: sB is a queue of capacity 1 filled at 0 by another source; qA gets X (→ B) then Y (→ C) at 1.
        SimulationConfig Blocked(string access)
        {
            var c = Line(1, (1, 1), (1, 2));
            c.Elements.RemoveAll(e => e.Id == "sB");
            c.Elements.Add(Queue("sB", 1));
            c.Elements.Add(Batches("block", "Blocker", (0, 1)));
            c.Connections.Add(new ConnectionConfig("block", "sB", "General"));
            c.Elements.Find(e => e.Id == "milk").Set("originAccess", access);
            return c;
        }
        List<string> log = null;
        Build(Blocked("Any"), run => log = Arrivals(run, "sC"));
        // Y is a request too: loaded 1–2, A → C 2–22, unload 22–23.
        Check(string.Join(" ", log) == "sC@23", "free access: " + string.Join(" ", log));
        Build(Blocked("Head"), run => log = Arrivals(run, "sC"));
        Check(log.Count == 0, "head only: Y waits behind X (" + string.Join(" ", log) + ")");

        // Order by a property of the origin: two origins, qA at A and qB at B (property prio = 1), one carrier each at 1, both
        // for C. Capacity 1, opportunistic. By distance: qA first (A 1–2, → C 2–22, 22–23; C → B 23–33, 33–34, → C 34–44, 44–45).
        // By origin.prio desc: qB first (A → B 1–11, 11–12, → C 12–22, 22–23; C → A 23–43, 43–44, → C 44–64, 64–65).
        SimulationConfig TwoOrigins(string order)
        {
            var c = Line(1, (1, 2));
            c.Elements.Add(Queue("qB", 5));
            c.Elements.Find(e => e.Id == "qB").Set("properties", new List<PropertySettings> { PropertySettings.Number("prio", 1) });
            var t2 = new Dictionary<string, List<string>> { ["Time"] = new List<string> { "1" }, ["Name"] = new List<string> { "Box" }, ["Q"] = new List<string> { "1" }, ["d"] = new List<string> { "2" } };
            var s2 = El("srcB", "Source"); s2.Set("mode", "Schedule"); s2.Set("arrivalsTable", t2);
            c.Elements.Add(s2);
            c.Connections.Add(new ConnectionConfig("srcB", "qB", "General"));
            var p = c.Elements.Find(e => e.Id == "milk");
            p.Set("pickups", new List<MilkRunPickupSettings> { MilkRunPickupSettings.Of("A", "qA"), MilkRunPickupSettings.Of("B", "qB") });
            (p.Get<List<MilkRunFlowSettings>>("flows")).Add(MilkRunFlowSettings.Of("bToC", "qB", "C", "sC", 0, null, null, "item.d == 2"));
            p.Set("requestOrder", order);
            return c;
        }
        Build(TwoOrigins("Nearest"), run => log = Arrivals(run, "sC"));
        Check(string.Join(" ", log) == "sC@23 sC@45", "nearest first: " + string.Join(" ", log));
        Build(TwoOrigins("|origin.prio desc"), run => log = Arrivals(run, "sC"));
        Check(string.Join(" ", log) == "sC@23 sC@65", "origin property first: " + string.Join(" ", log));
    }

    static void TestUrgencyOrder()
    {
        // Multi-key orders in the text form: each key keeps its direction; a single key is unchanged.
        var spec = SimuLean.Collections.ListQuerySpec.Decode("|urgent desc, distance");
        Check(spec.orderBy == "urgent desc, distance" && !spec.descending, "multi-key order decoded: " + spec.orderBy);
        Check(spec.Encode() == "|urgent desc, distance", "multi-key order round trip: " + spec.Encode());
        var single = SimuLean.Collections.ListQuerySpec.Decode("|origin.content DESC");
        Check(single.orderBy == "origin.content" && single.descending && single.Encode() == "|origin.content desc", "single key, DESC in any case");

        // qA at A (capacity 10): one carrier at 1 for C; qB at B (capacity 2): two carriers at 1 for C (full). Capacity 1,
        // opportunistic, the train at A. Nearest: qA (A 1–2, → C 2–22, 22–23), then qB twice (C → B 23–33, 33–34, → C 34–44,
        // 44–45; C → B 45–55, 55–56, → C 56–66, 66–67). Urgent first (originFill ≥ 0.8), then nearest: qB is full → B first
        // (A → B 1–11, 11–12, → C 12–22, 22–23); then qB holds 1 of 2 (0.5, not urgent): nearest from C = B (23–33, 33–34,
        // → C 34–44, 44–45); then qA (C → A 45–65, 65–66, → C 66–86, 86–87).
        SimulationConfig TwoOrigins(string order)
        {
            var c = Line(1, (1, 2));
            c.Elements.Add(Queue("qB", 2));
            var t2 = new Dictionary<string, List<string>> { ["Time"] = new List<string> { "1" }, ["Name"] = new List<string> { "Box" }, ["Q"] = new List<string> { "2" }, ["d"] = new List<string> { "2" } };
            var s2 = El("srcB", "Source"); s2.Set("mode", "Schedule"); s2.Set("arrivalsTable", t2);
            c.Elements.Add(s2);
            c.Connections.Add(new ConnectionConfig("srcB", "qB", "General"));
            var p = c.Elements.Find(e => e.Id == "milk");
            p.Set("pickups", new List<MilkRunPickupSettings> { MilkRunPickupSettings.Of("A", "qA"), MilkRunPickupSettings.Of("B", "qB") });
            (p.Get<List<MilkRunFlowSettings>>("flows")).Add(MilkRunFlowSettings.Of("bToC", "qB", "C", "sC", 0, null, null, "item.d == 2"));
            p.Set("requestFields", new List<MilkRunRequestFieldSettings> { MilkRunRequestFieldSettings.Of("urgent", "originFill >= 0.8") });
            (p.Get<List<NamedOrderSettings>>("namedOrders")).Add(NamedOrderSettings.Of("NearestUrgent", "|urgent desc, distance"));
            p.Set("requestOrder", order);
            return c;
        }
        List<string> log = null;
        Build(TwoOrigins("Nearest"), run => log = Arrivals(run, "sC"));
        Check(string.Join(" ", log) == "sC@23 sC@45 sC@67", "nearest: " + string.Join(" ", log));
        var fields = new List<string>();
        Build(TwoOrigins("NearestUrgent"), run =>
        {
            log = Arrivals(run, "sC");
            run.Clock.ScheduleEvent(new At(() =>
            {
                var req = run.Milk.Requests;
                foreach (var e in req.Ordered(SimuLean.Collections.ListQuery.All))
                {
                    var r = (MilkRunRequest)e.Value;
                    fields.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}/{2}/{3}", r.Origin.Name,
                        req.GetNumber(e, "originFill"), req.GetNumber(e, "origin.free"), req.GetNumber(e, "urgent")));
                }
            }), 1.5);
        });
        Check(string.Join(" ", log) == "sC@23 sC@45 sC@87", "urgent first, then nearest: " + string.Join(" ", log));
        // at 1.5 the train is on its way to B (AtStop: nothing assigned yet): qA holds 1 of 10, qB 2 of 2 (urgent)
        Check(string.Join(" ", fields) == "qA:0.1/9/0 qB:1/0/1 qB:1/0/1", "request fields originFill / origin.free / urgent: " + string.Join(" ", fields));

        // bad record fails loudly
        var bad = TwoOrigins("NearestUrgent");
        bad.Elements.Find(e => e.Id == "milk").Set("requestFields", new List<MilkRunRequestFieldSettings> { new MilkRunRequestFieldSettings { Name = "noformula" } });
        bool threw = false;
        try { Build(bad); } catch (FormatException) { threw = true; }
        Check(threw, "a request field without '=' fails");
    }

    static void TestShuttleStatistics()
    {
        var c = FillLine("Loading", -1, (0, 2), (0, 1));
        c.MaxSimulationTime = 40;
        var r = Build(c);
        // X on board 1–23, Y 2–34: integral 22 + 32 = 54 over 40; distance A → C 20 + C → B 10.
        Near(r.Milk.LoadOnBoard.GetTimeWeightedAverage(40), 54 / 40.0, "load on board (time-weighted)");
        Near(r.Milk.DistanceTraveled, 30, "distance of the legs");
        var res = RunResults.Capture(r.Factory, "line");
        var m = res.Find(ResultScope.Element, "milk");
        Check(m != null && m.TryGet(ResultMetrics.LoadAvg, out double la) && Math.Abs(la - 54 / 40.0) < 1e-9, "RunResults: load_avg");
        Check(m != null && m.TryGet(ResultMetrics.Distance, out double di) && Math.Abs(di - 30) < 1e-9, "RunResults: distance");
        var sc = res.Find(ResultScope.Element, "sC");
        var sb = res.Find(ResultScope.Element, "sB");
        string tis = ResultMetrics.Tally("time_in_system", ResultMetrics.TallyMean);
        Check(sc != null && sc.TryGet(tis, out double tc) && Math.Abs(tc - 23) < 1e-9, "time in system at sC = 23 (a sink tally)");
        Check(sb != null && sb.TryGet(tis, out double tb) && Math.Abs(tb - 34) < 1e-9, "time in system at sB = 34 (a sink tally)");

        // Spatial (axes, 2 m/s, 1 m/s²): A(0) → C(26 m) = 26 / 2 + 2 = 15 s; distance = metres.
        var c3 = FillLine("Loading", -1, (0, 2));
        c3.Network = new NetworkConfig();
        foreach (var n in new[] { ("nA", 0.0), ("nB", 10.0), ("nC", 26.0) }) c3.Network.Nodes.Add(NetworkNodeConfig.Of(n.Item1, n.Item2, 0, 0));
        var t = c3.ResourceTeams[0]; t.Travel = TravelKind.Axes; t.AxisSpeed = new double[] { 2, 2, 2 }; t.AxisAccel = new double[] { 1, 1, 1 }; t.HomeNode = "nA";
        var p = c3.Elements.Find(e => e.Id == "milk");
        p.Remove("legs");
        p.Set("depotNode", "nA");
        p.Set("stops", new List<MilkRunStopSettings> { MilkRunStopSettings.Of("B", "nB", null), MilkRunStopSettings.Of("C", "nC", null) });
        p.Set("fillTimeout", 0.0);
        List<string> log = null;
        var r3 = Build(c3, run => log = Arrivals(run, "sC"));
        // load 0–1, A → C 1–16, unload 16–17
        Check(string.Join(" ", log) == "sC@17", "axes travel with acceleration: " + string.Join(" ", log));
        Near(r3.Milk.DistanceTraveled, 26, "distance in metres (axes)");
    }

    static void TestNamedOrders()
    {
        var c = FillLine("Loading", -1, (0, 2));
        var milk = c.Elements.Find(e => e.Id == "milk");
        milk.Set("requestOrder", "Nearest");
        var info = SimuLean.Optimization.ModelParameterCatalog.ForElement(milk);
        var ro = info.FirstOrDefault(i => i.Key == "requestOrder");
        var dor = info.FirstOrDefault(i => i.Key == "deliveryOrder");
        Check(ro != null && ro.Kind == SimuLean.Optimization.VariableKind.Choice && string.Join(",", ro.Options) == "Loading,Nearest" && ro.CurrentText == "Nearest",
              "request order offered as a choice of the named orders");
        Check(dor != null && dor.CurrentText == "Loading", "delivery order offered too");
        Check(info.Any(i => i.Key == "policy") && info.Any(i => i.Key == "assignment") && info.Any(i => i.Key == "fillTimeout")
              && info.Any(i => i.Key == "capacity"), "policy, assignment, fill time-out and capacity are parameters");
        milk.Set("requestOrder", "|distance");
        var inPlaceOrder = SimuLean.Optimization.ModelParameterCatalog.ForElement(milk).FirstOrDefault(i => i.Key == "requestOrder");
        Check(inPlaceOrder != null && inPlaceOrder.IsText && inPlaceOrder.CurrentText == "|distance", "an order written in place is a text setting, not a choice");
        Check(SimuLean.Optimization.ModelParameterCatalog.Undecided(ReferenceModels.MilkRunShuttleLine()).Count == 0, "shuttle model: every key decided");

        // the reference model: deterministic, conserves items, never waits HELD (room counted with the assigned carriers)
        (Dictionary<string, Element> el, TaskExecutor member) RunRef()
        {
            var cfg = ReferenceModels.MilkRunShuttleLine();
            var clock = new SimClock(cfg.Seed);
            var f = new HeadlessModelFactory(clock);
            var el = f.BuildModel(cfg);
            f.StartModel();
            clock.AdvanceClock(cfg.MaxSimulationTime);
            return (el, f.Executors[0]);
        }
        var r1 = RunRef();
        var r2 = RunRef();
        double th1 = r1.el["D1"].GetStatsInputCount() + r1.el["D2"].GetStatsInputCount();
        double th2 = r2.el["D1"].GetStatsInputCount() + r2.el["D2"].GetStatsInputCount();
        Check(th1 > 150 && th1 == th2, "deliveries in the hour after the warm-up: " + th1);
        Near(r1.member.GetStateRatio(ResourceStates.HELD), 0, "never HELD at a full feed queue");
        Check(r1.el["Feed"].GetStatsContentMax() <= 5, "feed queue within its capacity");
    }

    static void TestErrors()
    {
        bool Fails(Action<SimulationConfig> change)
        {
            var c = Basic();
            change(c);
            try { Build(c); return false; } catch (Exception) { return true; }
        }
        var milk = (Func<SimulationConfig, ElementConfig>)(c => c.Elements.Find(e => e.Id == "milk"));
        Check(Fails(c => milk(c).Remove("transportBy.team")), "no team");
        Check(Fails(c => milk(c).Set("flows", new List<MilkRunFlowSettings> { MilkRunFlowSettings.Of("f", "nowhere", "S1", "lineA") })), "unknown origin");
        Check(Fails(c => milk(c).Set("flows", new List<MilkRunFlowSettings> { MilkRunFlowSettings.Of("f", "orgA", "S9", "lineA") })), "unknown stop");
        Check(Fails(c => milk(c).Set("pattern", new List<string> { "X" })), "unknown tour type in the pattern");
        Check(Fails(c => milk(c).Set("departure", "Schedule")), "schedule without interval");
        Check(Fails(c => milk(c).Set("stops", new List<MilkRunStopSettings> { MilkRunStopSettings.Of("S1", "", new[] { "orgA" }), MilkRunStopSettings.Of("S2", "", null) })),
              "empties without a destination");
    }

    // a calculated request field reads the model's
    // elements (content('X') / free('X') / get('X', 'p')) as every other expression does; get('m', 'state') reads the element's
    // state, so a reaction on StateChanged is no longer needed to keep it in a property.
    static void TestRequestFieldsReach()
    {
        SimulationConfig WithField(string formula)
        {
            var c = FillLine("Loading", -1, (0, 2), (0, 1));
            var p = c.Elements.Find(e => e.Id == "milk");
            p.Set("requestFields", new List<MilkRunRequestFieldSettings> { MilkRunRequestFieldSettings.Of("f", formula) });
            (p.Get<List<NamedOrderSettings>>("namedOrders")).Add(NamedOrderSettings.Of("ByF", "|f"));
            p.Set("deliveryOrder", "ByF");
            return c;
        }
        List<string> log = null;
        Build(WithField("distance * 2"), run => log = Arrivals(run, "sB", "sC"));
        Check(string.Join(" ", log) == "sB@13 sC@24", "a formula over the request's own fields works (nearest first): " + string.Join(" ", log));
        foreach (string formula in new[] { "content(destination.name)", "destination.content", "content('sB')", "free('sC')", "get('sB', 'content')" })
        {
            var cfg = WithField(formula);
            var f = new HeadlessModelFactory(new SimClock(cfg.Seed));
            f.BuildModel(cfg);
            string error = null;
            try { Build(cfg); } catch (InvalidOperationException ex) { error = ex.Message; }
            Check(error == null, $"'{formula}': a request field reads other elements: {error}");
        }
        // origin.x in a formula reads the origin, as in an order key; a property no element has fails the build
        string parse = null;
        try { Build(WithField("origin.prio + origin.content")); } catch (FormatException ex) { parse = ex.Message; }
        Check(parse != null && parse.Contains("no element has a property 'prio'"), "origin.x is checked when the model is built: " + parse);

        // a machine's state: src → router (routing expression) → m1 | m2 (5 each); parts at 0, 1, 2
        string Routed(string expr, bool busyReactions)
        {
            var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 50 };
            c.Elements.Add(Batches("src", "P", (0, 1), (1, 1), (2, 1)));
            var router = Server("router", 0); router.Set("capacity", 10);
            router.Set("outputStrategy.strategyType", "Expression"); router.Set("outputStrategy.routingExpression", expr);
            c.Elements.Add(router); c.Elements.Add(Server("m1", 5)); c.Elements.Add(Server("m2", 5)); c.Elements.Add(El("sink", "Sink"));
            foreach (var (a, b) in new[] { ("src", "router"), ("router", "m1"), ("router", "m2"), ("m1", "sink"), ("m2", "sink") })
                c.Connections.Add(new ConnectionConfig(a, b, "General"));
            if (busyReactions)
                foreach (var (id, state, v) in new[] { ("busyOn", "PROCESSING", 1), ("busyOff", "IDLE", 0) })
                {
                    var r = new ReactionConfig(id, "m1");
                    r.Set("signal", "StateChanged"); r.Set("state", state); r.Set("timing", "Immediate");
                    r.AddAction("SetElementProperty", target: "m1", name: "busy", value: v);
                    c.Reactions.Add(r);
                }
            var clock = new SimClock(1);
            var f = new HeadlessModelFactory(clock);
            var el = f.BuildModel(c);
            var seen = new List<string>();
            foreach (var m in new[] { "m1", "m2" }) { string n = m; el[m].ItemEntered += (e, it) => seen.Add(n + "@" + clock.GetSimulationTime().ToString(CultureInfo.InvariantCulture)); }
            f.StartModel();
            clock.AdvanceClock(50);
            return string.Join(" ", seen);
        }
        string s1 = Routed("get('m1', 'state') == 'PROCESSING' ? 1 : 0", false);
        Check(s1 == "m1@0 m2@1 m1@5", "get('m1', 'state') reads its state: the second part goes to m2 (" + s1 + ")");
        string s2 = Routed("content('m1') < 1 ? 0 : 1", false);
        Check(s2 == "m1@0 m2@1 m1@5", "content('m1') as a busy proxy: the second part goes to m2 (" + s2 + ")");
        string s3 = Routed("get('m1', 'busy') == 1 ? 1 : 0", true);
        Check(s3 == "m1@0 m2@1 m1@5", "a property kept by reactions on StateChanged: the same (" + s3 + ")");
    }
}
