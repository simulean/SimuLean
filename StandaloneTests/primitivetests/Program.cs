// Standalone tests of the resource primitives: resources from several teams,
// taken all at once or in order (the earlier ones held); a station reserving room downstream before a job starts;
// a station securing its process resources before it takes the part. Defaults keep the behaviour before them (the
// reference baselines guard that); the reference model `resource_primitives` combines the three.
// Run: dotnet run  (from StandaloneTests/primitivetests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;
using SimuLean.Results;

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
        Run("Several teams for one job, all at once across two teams: nothing held while the other team's member is busy", TestAllAtOnceAcrossTeams);
        Run("Several teams for one job, in order: the crane is taken first and held (HELD) while the driver is waited for", TestInOrderHolds);
        Run("Several teams for one job, in order with preemption: the driver part takes a member from lower work; a member held waiting can be taken", TestInOrderPreemption);
        Run("Several teams for one job, hold-and-wait: two uses taking two teams in opposite orders deadlock; the pending parts are listed", TestHoldAndWaitDeadlock);
        Run("Several teams for one job, statistics: requests waited per team, utilisation includes the time HELD; walking members", TestPartsStatisticsAndWalk);
        Run("Several teams for one job, config: requirement team and acquisition order from the keys; errors; transport refuses another team", TestPartsConfig);
        Run("Several teams for one job, assembly station: per-task requirements of their own (another team), the station's use for the others", TestAssemblyPerTask);
        Run("Exit reservation: a job starts only when a place downstream can be reserved: no item waits BLOCKED after its process", TestReserveExitLine);
        Run("Exit reservation: the reserved place is honoured by another origin's general link; several slots reserve one place each", TestReserveExitHonoured);
        Run("Exit reservation: a routing rule is evaluated at the start and kept: the item waits for its named destination", TestReserveExitRule);
        Run("Exit reservation: through a carrier link: the same promise is handed over (counted once); the Combiner; a queue refuses the key", TestReserveExitTransportAndStations);
        Run("Securing resources before the part: the part stays upstream until the operator is at the station (walking); never WAITING_FOR_RESOURCE with it", TestAcquireBeforeEntryWalk);
        Run("Securing resources before the part: the grant waits for the next item when the offered one went elsewhere; setups (own crew, kept); config", TestAcquireBeforeEntryRules);
        Run("The max wait ends when the part enters — a job waiting for room downstream never gives up its operator", TestEntryEndsWait);
        Run("A setup crew's own job gets the setup's work, so it can be preempted; the process operator HELD meanwhile", TestSetupCrewPreempted);
        Run("Nothing is asked for an item the station refuses (admission first; state checks have no side effects)", TestAdmissionFirst);
        Run("Securing resources before the part: only the Multi-Server; Reroute and a list input are refused at the build", TestAcquireBeforeEntryRefusals);
        Run("Timeouts read 0 without a limit; a setup crew's interruptions; team series by name; team states checked", TestTimeoutAndTeamResults);
        Run("An unknown downtime key fails the build, naming it and the valid keys; every key the builders read is known", TestUnknownDowntimeKey);
        Run("A use with no team whose requirements name theirs is active; a team-less requirement then fails the build", TestRequirementsOnlyUse);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    // ── harness ──────────────────────────────────────────────────────────

    sealed class At : IEvent { readonly Action a; public At(Action a) { this.a = a; } public void Execute() => a(); }
    static void Schedule(SimClock c, double t, Action a) => c.ScheduleEvent(new At(a), t - c.GetSimulationTime());
    static IRandomSampler C(double v) => new ConstantRandomProcess(v);

    /// <summary>An upstream that never sends (stations get their items by Receive).</summary>
    sealed class Up : Element
    {
        public Up(SimClock c, string n) : base(n, c) { }
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
        public override bool Unblock() => false;
    }

    static void Link(Element from, Element to) => GeneralLink.CreateLink(from, new List<Element> { to });

    static MultiServer Station(SimClock clock, string name, double time, StationResources use, Element sink = null)
    {
        var s = new MultiServer(new IRandomSampler[] { C(time) }, name, clock) { Resources = use };
        Link(new Up(clock, "Up" + name), s);
        Link(s, sink ?? new Sink("Sink" + name, clock));
        return s;
    }

    static (ResourceDispatcher d, TaskExecutor[] m) Team(SimClock clock, string name, int n)
    {
        var d = new ResourceDispatcher(name, clock);
        var m = new TaskExecutor[n];
        for (int i = 0; i < n; i++) { m[i] = new TaskExecutor(name + (i + 1), clock); d.Add(m[i]); }
        return (d, m);
    }

    static void StartAll(IEnumerable<ResourceDispatcher> teams, params Element[] elements)
    {
        foreach (var e in elements) e.Start();
        foreach (var d in teams) { foreach (var m in d.Members) m.Start(); d.Start(); }
    }

    static double LastEntry(Element e) => e.StatsCollector.LastEntryTime;
    static Element SinkOf(MultiServer s) => ((GeneralLink)s.GetOutput()).Outputs[0];

    // ── several teams ──────────────────────────────────────────────────────────────

    static void TestAllAtOnceAcrossTeams()
    {
        var clock = new SimClock(1);
        var (cranes, c) = Team(clock, "Crane", 1);
        var (drivers, dr) = Team(clock, "Driver", 1);
        // lift: a crane (its own team) and a driver (the drivers' team), all at once (default)
        var lift = Station(clock, "Lift", 5, new StationResources(cranes, new ResourceRequirement(), new ResourceRequirement(1, null, "driver", drivers)));
        var drive = Station(clock, "Drive", 10, new StationResources(drivers));   // the driver busy 0–10
        var hook = Station(clock, "Hook", 3, new StationResources(cranes));        // the crane alone
        StartAll(new[] { cranes, drivers }, lift, drive, hook, SinkOf(lift), SinkOf(drive), SinkOf(hook));
        drive.Receive(new Item(0));
        Schedule(clock, 1, () => lift.Receive(new Item(1)));
        Schedule(clock, 2, () => hook.Receive(new Item(2)));
        double craneStateAt1_5 = -1;
        Schedule(clock, 1.5, () => craneStateAt1_5 = c[0].IsAvailable ? 1 : 0);
        clock.AdvanceClock(40);
        Check(craneStateAt1_5 == 1, "the crane stays free while the driver is busy (nothing held)");
        Near(LastEntry(SinkOf(hook)), 5, "the crane alone serves Hook at once (2–5)");
        Near(LastEntry(SinkOf(lift)), 15, "the lift starts when both are free (10) and ends at 15");
        Check(lift.Resources.UsesParts && cranes.Requests.Count == 0 && drivers.Requests.Count == 0, "parts served, nothing pending");
        Near(c[0].GetTimeInState(ResourceStates.HELD), 0, "the crane was never held");
    }

    static void TestInOrderHolds()
    {
        var clock = new SimClock(1);
        var (cranes, c) = Team(clock, "Crane", 1);
        var (drivers, dr) = Team(clock, "Driver", 1);
        var use = new StationResources(cranes, new ResourceRequirement(), new ResourceRequirement(1, null, "driver", drivers)) { Order = AcquireOrder.InOrder };
        var lift = Station(clock, "Lift", 5, use);
        var drive = Station(clock, "Drive", 10, new StationResources(drivers));
        var hook = Station(clock, "Hook", 3, new StationResources(cranes));
        StartAll(new[] { cranes, drivers }, lift, drive, hook, SinkOf(lift), SinkOf(drive), SinkOf(hook));
        drive.Receive(new Item(0));
        Schedule(clock, 1, () => lift.Receive(new Item(1)));
        Schedule(clock, 2, () => hook.Receive(new Item(2)));
        ElementState craneAt5 = default, liftAt5 = default;
        Schedule(clock, 5, () => { craneAt5 = c[0].CurrentState; liftAt5 = lift.CurrentState; });
        clock.AdvanceClock(40);
        Check(craneAt5 == ResourceStates.HELD, $"the crane is HELD at the station while the driver is waited for ({craneAt5.Name})");
        Check(liftAt5 == ElementState.WAITING_FOR_RESOURCE, "the lift waits for its resource meanwhile");
        Near(LastEntry(SinkOf(lift)), 15, "the lift runs 10–15 (the driver free at 10)");
        Near(LastEntry(SinkOf(hook)), 18, "Hook waits for the crane until the lift ends (15–18)");
        Near(c[0].GetTimeInState(ResourceStates.HELD), 9, "the crane held 1–10");
        Near(c[0].GetTimeInState(ElementState.PROCESSING), 5 + 3, "then processing the lift and the hook");
    }

    static void TestInOrderPreemption()
    {
        var clock = new SimClock(1);
        var (cranes, c) = Team(clock, "Crane", 1);
        var (drivers, dr) = Team(clock, "Driver", 1);
        var use = new StationResources(cranes, new ResourceRequirement(), new ResourceRequirement(1, null, "driver", drivers))
        { Order = AcquireOrder.InOrder, Priority = _ => 5, Preempt = PreemptMode.Resume };
        var lift = Station(clock, "Lift", 5, use);
        var drive = Station(clock, "Drive", 10, new StationResources(drivers) { Preempt = PreemptMode.Resume });
        StartAll(new[] { cranes, drivers }, lift, drive, SinkOf(lift), SinkOf(drive));
        drive.Receive(new Item(0));
        Schedule(clock, 1, () => lift.Receive(new Item(1)));
        clock.AdvanceClock(40);
        Near(LastEntry(SinkOf(lift)), 6, "the lift takes the driver from the lower work at 1 and ends at 6");
        Near(LastEntry(SinkOf(drive)), 15, "the drive resumes its remaining 9 at 6");
        Check(dr[0].Preemptions == 1 && drive.Resources.Interruptions == 1, "one preemption of the driver");

        // a member held waiting for the next part is taken by a request of higher priority; the job asks again
        clock = new SimClock(1);
        (cranes, c) = Team(clock, "Crane", 1);
        (drivers, dr) = Team(clock, "Driver", 1);
        use = new StationResources(cranes, new ResourceRequirement(), new ResourceRequirement(1, null, "driver", drivers)) { Order = AcquireOrder.InOrder };
        lift = Station(clock, "Lift", 5, use);
        drive = Station(clock, "Drive", 10, new StationResources(drivers));
        var urgent = Station(clock, "Urgent", 2, new StationResources(cranes) { Priority = _ => 9, Preempt = PreemptMode.Resume });
        StartAll(new[] { cranes, drivers }, lift, drive, urgent, SinkOf(lift), SinkOf(drive), SinkOf(urgent));
        drive.Receive(new Item(0));
        Schedule(clock, 1, () => lift.Receive(new Item(1)));
        Schedule(clock, 3, () => urgent.Receive(new Item(2)));
        clock.AdvanceClock(40);
        Near(LastEntry(SinkOf(urgent)), 5, "the urgent job takes the held crane at 3 (3–5)");
        Near(LastEntry(SinkOf(lift)), 15, "the lift gets the crane back at 5, the driver at 10: 10–15");
        Check(c[0].Preemptions == 1 && lift.Resources.Interruptions == 1, "the held crane counted as preempted once");
        Near(c[0].GetTimeInState(ResourceStates.HELD), 2 + 5, "held 1–3, then 5–10");
    }

    static void TestHoldAndWaitDeadlock()
    {
        // c uses the driver 1–6; a (crane, then driver) arrives at 2 and holds the crane; b (driver, then crane; priority 5,
        // the drivers serve by priority) arrives at 3; at 6 b gets the driver and asks for the crane a holds: a deadlock
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 3, MaxSimulationTime = 500, StopWhenNoProgressFor = 100 };
        void Station(string n, double gap)
        {
            var src = new ElementConfig("src" + n, "Source", "src" + n);
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", gap);
            var s = new ElementConfig(n, "MultiServer", n);
            s.Set("capacity", 1); s.Set("delayStrategy.strategyType", "Constant"); s.Set("delayStrategy.constantValue", 5.0);
            c.Elements.Add(src); c.Elements.Add(s);
            c.Connections.Add(new ConnectionConfig("src" + n, n, "General"));
            c.Connections.Add(new ConnectionConfig(n, "sink", "General"));
        }
        Station("c", 1); Station("a", 2); Station("b", 3);
        c.Elements.Add(new ElementConfig("sink", "Sink", "sink"));
        foreach (var t in new[] { "cranes", "drivers" })
        {
            var tc = new ResourceTeamConfig { Id = t, Name = t, RequestOrder = "|priority desc" };
            tc.Members.Add(new ResourceMemberConfig { Name = t + "1" });
            c.ResourceTeams.Add(tc);
        }
        var pc = c.Elements.Find(x => x.Id == "c");
        pc.Set("processBy.team", "drivers");
        var a = c.Elements.Find(x => x.Id == "a");
        a.Set("processBy.team", "cranes"); a.Set("processBy.acquire", "InOrder");
        a.Set("processBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1), RequirementSettings.Of(1, team: "drivers") });
        var b = c.Elements.Find(x => x.Id == "b");
        b.Set("processBy.team", "drivers"); b.Set("processBy.acquire", "InOrder"); b.Set("processBy.priority", "5");
        b.Set("processBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1), RequirementSettings.Of(1, team: "cranes") });
        var clock = new SimClock(3);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        var old = SimLog.WarningSink; SimLog.WarningSink = w => { };
        try { clock.AdvanceClock(c.MaxSimulationTime); } finally { SimLog.WarningSink = old; }
        var run = clock.Context.Run;
        Check(run.Deadlocked || run.NoProgress, $"the run reports the hold-and-wait (deadlocked {run.Deadlocked}, no progress {run.NoProgress})");
        Check(clock.GetSimulationTime() < 200, $"it ends early ({clock.GetSimulationTime()})");
        var pending = model.Teams.Values.SelectMany(d => d.Requests.Entries.Select(e => e.Value as ResourceRequest))
                           .Where(r => r != null && r.State == RequestState.Pending).Select(r => r.Requester.Name).ToList();
        Check(pending.Contains("a") && pending.Contains("b"), $"the second part of a and of b are pending ({string.Join(", ", pending)})");
        Check(model.Executors.All(x => x.CurrentState == ResourceStates.HELD), "both members HELD, each by the other station");
        Check(run.Waiting.Exists(w => w.StartsWith("a: WAITING_FOR_RESOURCE")) && run.Waiting.Exists(w => w.StartsWith("b: WAITING_FOR_RESOURCE")),
              $"the waiting stations are named ({string.Join(" | ", run.Waiting)})");
    }

    static void TestPartsStatisticsAndWalk()
    {
        var clock = new SimClock(1);
        var (cranes, c) = Team(clock, "Crane", 1);
        var (drivers, dr) = Team(clock, "Driver", 1);
        var use = new StationResources(cranes, new ResourceRequirement(), new ResourceRequirement(1, null, "driver", drivers)) { Order = AcquireOrder.InOrder };
        var lift = Station(clock, "Lift", 5, use);
        var drive = Station(clock, "Drive", 10, new StationResources(drivers));
        StartAll(new[] { cranes, drivers }, lift, drive, SinkOf(lift), SinkOf(drive));
        drive.Receive(new Item(0));
        Schedule(clock, 1, () => lift.Receive(new Item(1)));
        clock.AdvanceClock(20);
        Near(c[0].Utilization * 20, 14, "the crane is busy (held then processing) 1–15");
        Near(drivers.Requests.WaitTime.Average, 4.5, "the drivers' requests waited: the lift's part 1→10 (9), the drive's none", 1e-9);
        Near(cranes.Requests.WaitTime.Average, 0, "the crane's part served at once");
        Check(c[0].UsageByRequester.TryGetValue("Lift", out double u) && Math.Abs(u - 14) < 1e-9, "the crane's usage by the lift: 14");

        // spatial members walk to the station; the process starts when both are there
        clock = new SimClock(1);
        var net = new SimuLean.Transport.TransportNetwork();
        net.AddNode("H", 0, 0, 0); net.AddNode("S", 4, 0, 0);
        net.AddTrack("H", "S", 4, true, SimuLean.Transport.TrackType.TravelPath);
        var d1 = new ResourceDispatcher("Cranes", clock); var d2 = new ResourceDispatcher("Drivers", clock);
        var crane = new TaskExecutor("Crane", clock, ResourceMode.Spatial) { HomeNode = "S", TravelModel = new NetworkTravel(net, 1) };
        var driver = new TaskExecutor("Driver", clock, ResourceMode.Spatial) { HomeNode = "H", TravelModel = new NetworkTravel(net, 1) };
        d1.Add(crane); d2.Add(driver);
        var s = Station(clock, "Lift", 5, new StationResources(d1, new ResourceRequirement(), new ResourceRequirement(1, null, null, d2)));
        s.Location = "S";
        StartAll(new[] { d1, d2 }, s, SinkOf(s));
        s.Receive(new Item(0));
        clock.AdvanceClock(20);
        Near(LastEntry(SinkOf(s)), 9, "the driver walks 4, then 5 of process");
        Near(crane.GetTimeInState(ResourceStates.HELD), 4, "the crane waits HELD at the station for the walker");
    }

    static void TestPartsConfig()
    {
        SimulationConfig Model(Action<ElementConfig> set, bool transport = false)
        {
            var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var src = new ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", 10.0);
            var s = new ElementConfig("s", "MultiServer", "s");
            s.Set("capacity", 1); s.Set("delayStrategy.strategyType", "Constant"); s.Set("delayStrategy.constantValue", 2.0);
            c.Elements.Add(src); c.Elements.Add(s); c.Elements.Add(new ElementConfig("sink", "Sink", "sink"));
            c.Connections.Add(new ConnectionConfig("src", "s", "General")); c.Connections.Add(new ConnectionConfig("s", "sink", "General"));
            foreach (var t in new[] { "cranes", "drivers" })
            {
                var tc = new ResourceTeamConfig { Id = t, Name = t };
                tc.Members.Add(new ResourceMemberConfig { Name = t + "1" });
                c.ResourceTeams.Add(tc);
            }
            set(transport ? src : s);
            return c;
        }
        HeadlessModelFactoryBuild(Model(p =>
        {
            p.Set("processBy.team", "cranes"); p.Set("processBy.acquire", "InOrder");
            p.Set("processBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1, "", "crane"), RequirementSettings.Of(1, "", "driver", "drivers") });
        }), out var model, out var clock);
        var use = ((MultiServer)model.Find("s")).Resources;
        Check(use.Order == AcquireOrder.InOrder && use.Requirements.Count == 2 && use.Requirements[1].Team == model.Teams["drivers"]
              && use.Requirements[0].Team == null, "InOrder and the second requirement's team built from the keys");
        clock.AdvanceClock(100);
        Check(model.Find("sink").GetStatsInputCount() >= 9, "the model runs");
        Check(ResourceBuild.EncodeRequirement(1, "", "x", "") == ResourceBuild.EncodeRequirement(1, "", "x"), "no team: the three-field record as before");

        bool Fails(SimulationConfig c, string what)
        {
            try { HeadlessModelFactoryBuild(c, out _, out _); return false; }
            catch (Exception e) when (e is FormatException || e is ArgumentException) { return e.Message.Contains(what); }
        }
        Check(((Func<bool>)(() => { try { Model(p => { p.Set("processBy.team", "cranes"); p.Set("processBy.acquire", "Sometimes"); }); return false; }
                                    catch (FormatException e) { return e.Message.Contains("Sometimes"); } }))(),
              "an unknown acquisition order fails naming it (when it is written: the setting is an option)");
        Check(Fails(Model(p => { p.Set("processBy.team", "cranes"); p.Set("processBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1, team: "nobody") }); }), "nobody"),
              "an unknown requirement team fails naming it");
        Check(Fails(Model(p => { p.Set("transportBy.team", "cranes"); p.Set("transportBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1, team: "drivers") }); }, true), "transport"),
              "a transport's requirement of another team fails the build");
    }

    static void HeadlessModelFactoryBuild(SimulationConfig c, out SimuLean.Headless.HeadlessModelFactory model, out SimClock clock)
    {
        clock = new SimClock(c.Seed);
        model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
    }

    static void TestAssemblyPerTask()
    {
        var clock = new SimClock(1);
        var (ops, o) = Team(clock, "Op", 1);
        var (cranes, c) = Team(clock, "Crane", 1);
        var specs = new List<AssemblyTaskSpec>
        {
            new AssemblyTaskSpec { Id = "fit", Duration = C(4) },
            // the lift needs the crane and the fitter, in order
            new AssemblyTaskSpec { Id = "lift", Predecessors = { "fit" }, Duration = C(3),
                                   Resources = new StationResources(ops, new ResourceRequirement(1, null, "crane", cranes), new ResourceRequirement())
                                   { Order = AcquireOrder.InOrder } },
        };
        var asm = new AssemblyStation("Asm", clock, specs, workerCount: 1) { Resources = new StationResources(ops) };
        var sink = new Sink("Sink", clock);
        Link(new Up(clock, "UpAsm"), asm);
        Link(asm, sink);
        StartAll(new[] { ops, cranes }, asm, sink);
        asm.Receive(new Item(0));
        clock.AdvanceClock(20);
        Near(LastEntry(sink), 7, "fit 0–4 with the station's operator, lift 4–7 with the crane and the operator");
        Near(c[0].GetTimeInState(ElementState.PROCESSING), 3, "the crane only for the lift");
        Near(o[0].GetTimeInState(ElementState.PROCESSING), 7, "the operator for both");

        // from the settings: each task's own requirements (tasks[i].requirements); the station's use for the rest
        var cfg = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
        var src = new ElementConfig("src", "Source", "src");
        src.Set("mode", "InterArrival");
        src.Set("interArrivalDistribution.strategyType", "Constant");
        src.Set("interArrivalDistribution.constantValue", 20.0);
        var asy = new ElementConfig("asy", "AssemblyStation", "asy");
        asy.Set("tasks", new List<AssemblyTaskSettings> { AssemblyTaskSettings.Of("fit", SamplerSettings.Constant(4), 0), AssemblyTaskSettings.Of("lift", SamplerSettings.Constant(3), 0, new[] { "fit" }) });
        asy.Set("tasks[1].processBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1, "", "crane", "cranes"), RequirementSettings.Of(1) });
        asy.Set("processBy.team", "ops"); asy.Set("processBy.acquire", "InOrder");
        cfg.Elements.Add(src); cfg.Elements.Add(asy); cfg.Elements.Add(new ElementConfig("sink", "Sink", "sink"));
        cfg.Connections.Add(new ConnectionConfig("src", "asy", "General")); cfg.Connections.Add(new ConnectionConfig("asy", "sink", "General"));
        foreach (var t in new[] { "ops", "cranes" })
        {
            var tc = new ResourceTeamConfig { Id = t, Name = t };
            tc.Members.Add(new ResourceMemberConfig { Name = t + "1" });
            cfg.ResourceTeams.Add(tc);
        }
        HeadlessModelFactoryBuild(cfg, out var model, out var mclock);
        var built = (AssemblyStation)model.Find("asy");
        var lift = built.Tasks.First(t => t.Id == "lift").Resources;
        Check(built.Tasks.First(t => t.Id == "fit").Resources == null && lift != null && lift.Order == AcquireOrder.InOrder
              && lift.Requirements.Count == 2 && lift.Requirements[0].Team == model.Teams["cranes"] && lift.Dispatcher == model.Teams["ops"],
              "the lift's own use: the crane of its team, then one of the station's team, in the station's order");
        mclock.AdvanceClock(100);
        var crane = model.Executors.First(x => x.Name == "cranes1");
        Near(crane.GetTimeInState(ElementState.PROCESSING), 4 * 3, "the crane works only the lifts (jobs at 20, 40, 60, 80: 4 × 3)");
        bool failedSet = false;
        try { asy.Set("tasks.requirements", new List<string> { "weld=1##" }); } catch (ArgumentException e) { failedSet = e.Message.Contains("tasks.requirements"); }
        Check(failedSet, "the old aggregated key is refused (each task's tasks[i].processBy)");

        // per-task settings (each task its own priority, preemption, acquisition order and max wait;
        // what a task does not set is the station's
        asy.Set("tasks[0].processBy.priority", "7");
        asy.Set("tasks[0].processBy.preemption", "Resume");
        asy.Set("tasks[1].processBy.preemptable", false);
        asy.Set("tasks[1].processBy.maxWait", "5");
        asy.Set("tasks[1].processBy.onTimeout", "GiveUp");
        asy.Set("processBy.priority", "2");
        HeadlessModelFactoryBuild(cfg, out var m2, out _);
        var b2 = (AssemblyStation)m2.Find("asy");
        var fitUse = b2.Tasks.First(t => t.Id == "fit").Resources;
        var liftUse = b2.Tasks.First(t => t.Id == "lift").Resources;
        Check(fitUse != null && fitUse.Preempt == PreemptMode.Resume && fitUse.Preemptable && fitUse.Order == AcquireOrder.InOrder
              && fitUse.Requirements.Count == b2.Resources.Requirements.Count && fitUse.Dispatcher == m2.Teams["ops"],
              "the fit's own priority and preemption, the station's team, requirements and order for the rest");
        Check(liftUse != null && !liftUse.Preemptable && liftUse.Preempt == PreemptMode.None && liftUse.MaxWait != null
              && liftUse.MaxWait.OnTimeout == TimeoutAction.GiveUp && liftUse.Requirements.Count == 2,
              "the lift's own requirements, not preemptable, its own max wait and action; the station's preemption");
        Check(b2.Resources.MaxWait == null, "the station's own use keeps no max wait");
        bool reroute = false;
        asy.Set("tasks[1].processBy.onTimeout", "Reroute");
        try { HeadlessModelFactoryBuild(cfg, out _, out _); } catch (FormatException e) { reroute = e.Message.Contains("lift") || e.Message.Contains("Reroute"); }
        Check(reroute, "a task cannot Reroute (only a Multi-Server's process)");
    }

    // ── securing resources first, in two steps ─────────────────

    // src (every 1) → q → S (secure first, reserve downstream, max wait 3, GiveUp; 2 per job) → buf (1 place) → D (20) → sink. The third job enters S
    // at 5 and waits there (BLOCKED) for a place in buf until 23. Before the fix its max wait ran on past the entry: it gave
    // up at 8, freed the operator, and S later processed with nobody there.
    static void TestEntryEndsWait()
    {
        foreach (string action in new[] { "GiveUp", "KeepWaiting" })
        {
            var c = Cfg(40);
            Src(c, "src", 1); Q(c, "q"); var s = Srv(c, "S", 2); Q(c, "buf", 1); Srv(c, "D", 20); Snk(c, "sink");
            Con(c, "src", "q"); Con(c, "q", "S"); Con(c, "S", "buf"); Con(c, "buf", "D"); Con(c, "D", "sink");
            TeamCfg(c, "ops", 1);
            s.Set("processBy.team", "ops"); s.Set("processBy.acquireBeforeEntry", true);
            s.Set("reserveExit", true);
            s.Set("processBy.maxWait", "3"); s.Set("processBy.onTimeout", action);
            var r = RunCfg(c);
            var st = r.model.Find("S");
            var op = r.model.Executors.Single();
            Check(st.Timeouts == 0, $"{action}: no timeout once the part is in ({st.Timeouts})");
            Near(op.GetTimeInState(ElementState.PROCESSING), st.GetTimeInState(ElementState.PROCESSING),
                 $"{action}: the operator is there for every process of S");
            Check(st.GetStatsOutputCount() >= 3, $"{action}: S goes on ({st.GetStatsOutputCount()} out)");
        }
    }

    // A: securing first (ops), a setup of 50 before each item by its own crew (setters). B needs a setter at priority 5 with Resume; its
    // item comes at 10, during A's setup: it takes the setter (B's stay 1), as it does without securing first. Before the fix the setup's
    // work went to A's process job, so the setter could not be preempted (B waited until 51).
    static void TestSetupCrewPreempted()
    {
        foreach (bool ahead in new[] { false, true })
        {
            var c = Cfg(80);
            Src(c, "srcA", 1); Q(c, "qA"); var a = Srv(c, "A", 1); Snk(c, "sinkA");
            Src(c, "srcB", 10); Q(c, "qB"); var b = Srv(c, "B", 1); Snk(c, "sinkB");
            Con(c, "srcA", "qA"); Con(c, "qA", "A"); Con(c, "A", "sinkA");
            Con(c, "srcB", "qB"); Con(c, "qB", "B"); Con(c, "B", "sinkB");
            TeamCfg(c, "ops", 1); TeamCfg(c, "setters", 1);
            a.Set("processBy.team", "ops");
            if (ahead) a.Set("processBy.acquireBeforeEntry", true);
            a.Set("setup.when", "EachItem"); a.Set("setup.time", "Constant~50"); a.Set("setup.setupBy.team", "setters");
            b.Set("processBy.team", "setters");
            b.Set("processBy.priority", "5"); b.Set("processBy.preemption", "Resume");
            var r = RunCfg(c, m => m.Find("sinkB").StatsCollector.RecordSamples());
            var outB = r.model.Find("sinkB").StatsCollector.EntryTimes;
            string tag = ahead ? "acquired before entry" : "acquired on entry";
            Check(outB.FirstOrDefault() == 11, $"{tag}: B's first item (10) takes the setter from A's setup and leaves at 11 ({Join(outB)})");
            var setter = r.model.Executors.First(x => x.Name == "setters1");
            Check(setter.Preemptions >= 1, $"{tag}: the setter was preempted ({setter.Preemptions})");
            var op = r.model.Executors.First(x => x.Name == "ops1");
            Near(op.GetTimeInState(ElementState.PROCESSING), r.model.Find("A").GetTimeInState(ElementState.PROCESSING),
                 $"{tag}: the process operator is PROCESSING only while A processes (not during the setup)");
        }
    }

    // Items labelled type = 'B' are offered to S, which admits only type == 'A' and secures its operator before the part:
    // nothing is asked (the operator is never HELD), the station takes nothing.
    static void TestAdmissionFirst()
    {
        var c = Cfg(100);
        var src = Src(c, "src", 1); src.Set("entryLabels", new List<string> { "type = 'B'" });
        Q(c, "q"); var s = Srv(c, "S", 1); Snk(c, "sink");
        Con(c, "src", "q"); Con(c, "q", "S"); Con(c, "S", "sink");
        TeamCfg(c, "ops", 1);
        s.Set("inputStrategy.strategyType", "Condition"); s.Set("inputStrategy.condition", "type == 'A'");
        s.Set("processBy.team", "ops"); s.Set("processBy.acquireBeforeEntry", true);
        var r = RunCfg(c);
        var op = r.model.Executors.Single();
        Check(r.model.Find("S").GetStatsInputCount() == 0, "S takes no item of type B");
        Near(op.GetTimeInState(ResourceStates.HELD), 0, "the operator is never HELD for an item S refuses");
        Check(((IResourceUser)r.model.Find("S")).Resources.GrantedAhead == 0 && !((IResourceUser)r.model.Find("S")).Resources.AskedAhead,
              "nothing asked, nothing granted");
    }

    static void TestAcquireBeforeEntryRefusals()
    {
        string BuildError(SimulationConfig c)
        {
            try { HeadlessModelFactoryBuild(c, out _, out _); return null; } catch (FormatException e) { return e.Message; }
        }
        foreach (string type in new[] { "Combiner", "PickingStation" })
        {
            var c = Cfg(10);
            var e = new ElementConfig("st", type, "st"); c.Elements.Add(e);
            TeamCfg(c, "ops", 1);
            e.Set("processBy.team", "ops"); e.Set("processBy.acquireBeforeEntry", true);
            string err = BuildError(c);
            Check(err != null && err.Contains("only the Multi-Server"), $"{type}: acquireBeforeEntry fails the build ({err})");
        }
        var cr = Cfg(10);
        Src(cr, "src", 1); var s = Srv(cr, "S", 1); Snk(cr, "sink"); Con(cr, "src", "S"); Con(cr, "S", "sink");
        TeamCfg(cr, "ops", 1);
        s.Set("processBy.team", "ops"); s.Set("processBy.acquireBeforeEntry", true);
        s.Set("processBy.maxWait", "3"); s.Set("processBy.onTimeout", "Reroute");
        s.Set("processBy.timeoutTarget", "sink");
        string rr = BuildError(cr);
        Check(rr != null && rr.Contains("Reroute") && rr.Contains("acquireBeforeEntry"), $"Reroute before the part fails the build ({rr})");
    }

    static void TestTimeoutAndTeamResults()
    {
        // the same model without securing first: the setter is preempted during A's setup
        var c = Cfg(80);
        Src(c, "srcA", 1); Q(c, "qA"); var a = Srv(c, "A", 1); Snk(c, "sinkA");
        Src(c, "srcB", 10); Q(c, "qB"); var b = Srv(c, "B", 1); Snk(c, "sinkB");
        Con(c, "srcA", "qA"); Con(c, "qA", "A"); Con(c, "A", "sinkA"); Con(c, "srcB", "qB"); Con(c, "qB", "B"); Con(c, "B", "sinkB");
        TeamCfg(c, "ops", 1, t => t.Name = "Operators"); TeamCfg(c, "setters", 1);
        a.Set("processBy.team", "ops");
        a.Set("setup.when", "EachItem"); a.Set("setup.time", "Constant~50"); a.Set("setup.setupBy.team", "setters");
        b.Set("processBy.team", "setters");
        b.Set("processBy.priority", "5"); b.Set("processBy.preemption", "Resume");
        HeadlessModelFactoryBuild(c, out var model, out var clock);
        var recorder = new SimuLean.Results.SeriesRecorder(model, 20);
        var old = SimLog.WarningSink; SimLog.WarningSink = w => { };
        try { recorder.RunTo(80); } finally { SimLog.WarningSink = old; }
        var results = SimuLean.Results.RunResults.Capture(model);
        recorder.AddTo(results);
        double Read(SimuLean.Optimization.MetricScope scope, string target, string stat)
            => SimuLean.Optimization.MetricCatalog.Read(new SimuLean.Optimization.ModelMetric("m", scope, target, stat), results);
        Near(Read(SimuLean.Optimization.MetricScope.Element, "qA", ResultMetrics.Timeouts), 0, "timeouts of a queue without a limit read 0");
        Near(Read(SimuLean.Optimization.MetricScope.Element, "A", ResultMetrics.TimeoutsOf(WaitKind.Resource)), 0, "timeouts:resource read 0");
        Check(Read(SimuLean.Optimization.MetricScope.Element, "A", ResultMetrics.Interruptions) >= 1,
              "A's interruptions count its setup crew's (the setter preempted by B)");
        Check(results.Series.Any(s => s.Scope == ResultScope.Team && s.Name == "Operators" && s.Metric == ResultMetrics.Utilization),
              $"a team's series are named as the results name it ({string.Join(", ", results.Series.Where(s => s.Scope == ResultScope.Team).Select(s => s.Name).Distinct())})");
        var bad = SimuLean.Optimization.MetricCatalog.Problem(new SimuLean.Optimization.ModelMetric("m", SimuLean.Optimization.MetricScope.Team, "Operators", "state:HLED"), results);
        var good = SimuLean.Optimization.MetricCatalog.Problem(new SimuLean.Optimization.ModelMetric("m", SimuLean.Optimization.MetricScope.Team, "Operators", "state:HELD"), results);
        Check(bad != null && bad.Contains("HLED") && good == null, $"a team's misspelt state is reported ({bad}; {good})");

        // an unknown option of a downtime fails the build (it was ignored: the default used)
        foreach (var (key, value) in new[] { (DowntimeKeys.Mode, "Immediatly"), (DowntimeKeys.Basis, "Wallclock") })
        {
            var cd = Cfg(10);
            Src(cd, "src", 1); Srv(cd, "S", 1); Snk(cd, "sink"); Con(cd, "src", "S"); Con(cd, "S", "sink");
            var dt = new DowntimeConfig("dt", "MtbfMttr", "S");
            string err = null;
            try { dt.Set(key, value); } catch (FormatException e) { err = e.Message; }   // typed settings: refused when written
            Check(err != null && err.Contains(value), $"{key} = {value} fails ({err})");
        }
    }

    static void TestUnknownDowntimeKey()
    {
        // every key the downtime builders read (DowntimeBuilder, ResourceBuild.RepairUse) is a known one
        foreach (string key in new[]
        {
            DowntimeKeys.State, DowntimeKeys.Mode, DowntimeKeys.BlockInput, DowntimeKeys.BlockOutput,
            DowntimeKeys.TimeToFailure, DowntimeKeys.TimeToRepair, DowntimeKeys.FirstFailure, DowntimeKeys.Basis,
            DowntimeKeys.BusyStates, DowntimeKeys.FailureCode, DowntimeKeys.ShiftPattern, DowntimeKeys.Holidays,
            DowntimeKeys.PeriodicFirst, DowntimeKeys.PeriodicPeriod, DowntimeKeys.PeriodicDuration, DowntimeKeys.PeriodicUntil,
            DowntimeKeys.AllowOverlap, DowntimeKeys.PeriodicCode, DowntimeKeys.Overlap, DowntimeKeys.Rows, DowntimeKeys.DataTable,
            DowntimeKeys.TargetColumn, DowntimeKeys.StartColumn, DowntimeKeys.EndColumn, DowntimeKeys.DurationColumn,
            DowntimeKeys.CodeColumn, DowntimeKeys.ReasonColumn, DowntimeKeys.DurationScale, DowntimeKeys.CodeRules, DowntimeKeys.TargetKeys,
            "repairBy.team", "repairBy.requirements", "repairBy.priority", "repairBy.preemption",
            "repairBy.preemptable", "repairBy.acquire", "repairBy.maxWait", "repairBy.onTimeout",
        })
            Check(SettingsPaths.Declares(typeof(DowntimeSettings), key), $"'{key}' is a downtime setting");
        Check(!SettingsPaths.Declares(typeof(DowntimeSettings), "processBy.team") && !SettingsPaths.Declares(typeof(DowntimeSettings), "transportBy.team"),
              "no element key is a downtime setting");

        // MTBF keys an earlier guide used: the build fails, naming the key and listing the valid ones (they were
        // silently ignored: the default ExponentialMean~3600 / ~300 were used)
        var cd = Cfg(100);
        Src(cd, "src", 1); Srv(cd, "S", 1); Snk(cd, "sink"); Con(cd, "src", "S"); Con(cd, "S", "sink");
        var dt = new DowntimeConfig("dt", "MtbfMttr", "S");
        string err = null;
        try { dt.Set("ttf", "Constant~10"); } catch (ArgumentException e) { err = e.Message; }
        Check(err != null && err.Contains("'ttf'") && err.Contains("'dt'"), $"ttf fails when written, naming it and the downtime ({err})");
        dt.Set(DowntimeKeys.TimeToRepair, "Constant~1");
        cd.Downtimes.Add(dt);

        // the right keys build, and the failure model is the one given (10 up, 1 down: 9 stops in 100)
        dt.Set(DowntimeKeys.TimeToFailure, "Constant~10");
        HeadlessModelFactoryBuild(cd, out var model, out var clock);
        model.StartModel();
        clock.AdvanceClock(100);
        var gen = model.DowntimeGenerators.Single();
        Check(gen.StopCount == 9, $"timeToFailure = Constant~10 gives 9 stops in 100 ({gen.StopCount})");

        // a periodic stop with an unknown key (the old 'period') fails too, the same way
        var cp = Cfg(100);
        Src(cp, "src", 1); Srv(cp, "S", 1); Snk(cp, "sink"); Con(cp, "src", "S"); Con(cp, "S", "sink");
        var pm = new DowntimeConfig("pm", "Periodic", "S");
        pm.Set(DowntimeKeys.PeriodicPeriod, 20.0); pm.Set(DowntimeKeys.PeriodicDuration, 2.0);
        err = null;
        try { pm.Set("first", 5.0); } catch (ArgumentException e) { err = e.Message; }
        Check(err != null && err.Contains("'first'") && err.Contains("'pm'"), $"a periodic 'first' fails, naming it and the downtime ({err})");
    }

    // processBy.team empty and requirements naming their teams — it was silently ignored (no resources at all)
    static void TestRequirementsOnlyUse()
    {
        SimulationConfig Model(Action<ElementConfig> set, Action<SimulationConfig> more = null)
        {
            var c = Cfg(100);
            Src(c, "src", 10); var s = Srv(c, "s", 2); Snk(c, "sink"); Con(c, "src", "s"); Con(c, "s", "sink");
            TeamCfg(c, "cranes", 1); TeamCfg(c, "drivers", 1);
            set(s); more?.Invoke(c);
            return c;
        }
        var (model, _) = RunCfg(Model(p => p.Set("processBy.requirements", new List<RequirementSettings>
            { RequirementSettings.Of(1, "", "crane", "cranes"), RequirementSettings.Of(1, "", "driver", "drivers") })));
        var use = ((MultiServer)model.Find("s")).Resources;
        Check(use != null && use.Requirements.Count == 2 && use.Dispatcher == model.Teams["cranes"],
              "no use team, two requirements with teams: the use is built (its first requirement's team stands for it)");
        Check(model.Teams["cranes"].Members[0].TasksDone >= 9 && model.Teams["drivers"].Members[0].TasksDone >= 9,
              "both teams work the station's jobs");

        string err = null;
        try { RunCfg(Model(p => p.Set("processBy.requirements", new List<RequirementSettings>
                { RequirementSettings.Of(1, "", "crane", "cranes"), RequirementSettings.Of(1, "", "helper") }))); }
        catch (FormatException e) { err = e.Message; }
        Check(err != null && err.Contains("processBy.requirements[1]") && err.Contains("set processBy.team"),
              $"a requirement without a team when the use has none fails, saying what to set ({err})");

        // the same rule for a transport and a setup crew
        (model, _) = RunCfg(Model(p => { }, c => c.Elements.Find(e => e.Id == "src")
            .Set("transportBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1, "", "", "drivers") })));
        Check(model.Teams["drivers"].Members[0].TasksDone > 0, "a transport use with only a requirement's team carries the items");
        (model, _) = RunCfg(Model(p =>
        {
            p.Set("setup.when", "EachItem"); p.Set("setup.time", "Constant~1");
            p.Set("setup.setupBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1, "", "", "cranes") });
        }));
        Check(model.Teams["cranes"].Members[0].TasksDone > 0, "a setup crew with only a requirement's team sets up");
        err = null;
        try
        {
            RunCfg(Model(p => { }, c =>
            {
                var dt = new DowntimeConfig("dt", "MtbfMttr", "s");
                dt.Set(DowntimeKeys.TimeToFailure, "Constant~30"); dt.Set(DowntimeKeys.TimeToRepair, "Constant~1");
                dt.Set("repairBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1, "", "", "cranes") });
                c.Downtimes.Add(dt);
            }));
        }
        catch (Exception e) { err = e.Message; }
        Check(err == null, $"a repair crew given only by a requirement's team builds ({err})");
        Check(ResourceBuild.IsActive("", new List<RequirementSettings> { RequirementSettings.Of(1, team: "x") })
              && !ResourceBuild.IsActive("", new List<RequirementSettings> { RequirementSettings.Of(1) }) && ResourceBuild.IsActive("t", null),
              "IsActive: a team, or a requirement's team");
    }

    // ── config helpers ───────────────────────────────────────

    static SimulationConfig Cfg(double horizon = 200) => new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = horizon };
    static ElementConfig Src(SimulationConfig c, string id, double gap)
    {
        var e = new ElementConfig(id, "Source", id);
        e.Set("mode", "InterArrival");
        e.Set("interArrivalDistribution.strategyType", "Constant");
        e.Set("interArrivalDistribution.constantValue", gap);
        c.Elements.Add(e); return e;
    }
    static ElementConfig Q(SimulationConfig c, string id, int cap = 1000)
    {
        var e = new ElementConfig(id, "Queue", id); e.Set("capacity", cap); c.Elements.Add(e); return e;
    }
    static ElementConfig Srv(SimulationConfig c, string id, double time, int cap = 1)
    {
        var e = new ElementConfig(id, "MultiServer", id);
        e.Set("capacity", cap); e.Set("delayStrategy.strategyType", "Constant"); e.Set("delayStrategy.constantValue", time);
        c.Elements.Add(e); return e;
    }
    static ElementConfig Snk(SimulationConfig c, string id) { var e = new ElementConfig(id, "Sink", id); c.Elements.Add(e); return e; }
    static void Con(SimulationConfig c, string a, string b) => c.Connections.Add(new ConnectionConfig(a, b, "General"));
    static void TeamCfg(SimulationConfig c, string id, int n, Action<ResourceTeamConfig> set = null)
    {
        var tc = new ResourceTeamConfig { Id = id, Name = id };
        for (int i = 0; i < n; i++) tc.Members.Add(new ResourceMemberConfig { Name = id + (i + 1) });
        set?.Invoke(tc);
        c.ResourceTeams.Add(tc);
    }
    static (SimuLean.Headless.HeadlessModelFactory model, SimClock clock) RunCfg(SimulationConfig c, Action<SimuLean.Headless.HeadlessModelFactory> before = null)
    {
        HeadlessModelFactoryBuild(c, out var model, out var clock);
        before?.Invoke(model);
        var old = SimLog.WarningSink; SimLog.WarningSink = w => { };
        try { clock.AdvanceClock(c.MaxSimulationTime); } finally { SimLog.WarningSink = old; }
        return (model, clock);
    }
    static string Join(IEnumerable<double> v) => string.Join(", ", v ?? new List<double>());

    // ── reserving room downstream ──────────────────────────────────────────────────────────────

    // src (every 1) → q → A (2) → B (10) → sink. Without the option A finishes its second job at 5 and holds it BLOCKED
    // until B frees (13); with it the second job waits for the place (BLOCKED before its process), runs 13–15, B 15–25.
    static void TestReserveExitLine()
    {
        List<double> Entries(bool reserve, out SimuLean.Headless.HeadlessModelFactory m)
        {
            var c = Cfg(40);
            Src(c, "src", 1); Q(c, "q"); var a = Srv(c, "A", 2); Srv(c, "B", 10); Snk(c, "sink");
            Con(c, "src", "q"); Con(c, "q", "A"); Con(c, "A", "B"); Con(c, "B", "sink");
            if (reserve) a.Set("reserveExit", true);
            var r = RunCfg(c, model => model.Find("B").StatsCollector.RecordSamples());
            m = r.model;
            return r.model.Find("B").StatsCollector.EntryTimes;
        }
        var off = Entries(false, out var mOff);
        var on = Entries(true, out var mOn);
        Check(off.Take(3).SequenceEqual(new double[] { 3, 13, 23 }), $"without: B takes at 3, 13, 23 ({Join(off)})");
        Check(on.Take(3).SequenceEqual(new double[] { 3, 15, 27 }), $"with: the second job runs 13–15 once B's place is reserved ({Join(on)})");
        var a = (MultiServer)mOn.Find("A");
        Near(a.Slots[0].GetTimeInState(ElementState.PROCESSING), 2 * 4, "A processes only when its item can go on (4 jobs by 40)");
        Check(mOn.Find("B").RoomReserved <= 1, "at most one place reserved at B");
    }

    static void TestReserveExitHonoured()
    {
        // A (reserve, 3) and C (no option, 1) both feed B (one place, 10); both get an item at 100. A's job reserves B's
        // place at 100; C finishes at 101 and must wait — the general link honours A's reservation — although B is free
        var c = Cfg(130);
        Src(c, "srcA", 100); Src(c, "srcC", 100);
        var a = Srv(c, "A", 3); Srv(c, "C", 1); Srv(c, "B", 10); Snk(c, "sink");
        a.Set("reserveExit", true);
        Con(c, "srcA", "A"); Con(c, "srcC", "C"); Con(c, "A", "B"); Con(c, "C", "B"); Con(c, "B", "sink");
        var r = RunCfg(c, model => model.Find("B").StatsCollector.RecordSamples());
        var entries = r.model.Find("B").StatsCollector.EntryTimes;
        Check(entries.Count >= 2 && entries[0] == 103 && entries[1] == 113, $"B takes A's item at 103 (reserved), C's at 113 ({Join(entries)})");

        // several slots: A of two slots, B of one place: one job reserves, the other waits BLOCKED before its process
        c = Cfg(40);
        Src(c, "src", 0.5); Q(c, "q"); var a2 = Srv(c, "A", 2, 2); Srv(c, "B", 10); Snk(c, "sink");
        a2.Set("reserveExit", true);
        Con(c, "src", "q"); Con(c, "q", "A"); Con(c, "A", "B"); Con(c, "B", "sink");
        r = RunCfg(c, model => model.Find("B").StatsCollector.RecordSamples());
        var ms = (MultiServer)r.model.Find("A");
        var bIn = r.model.Find("B").StatsCollector.EntryTimes;
        Check(bIn.Take(3).SequenceEqual(new[] { 2.5, 14.5, 26.5 }), $"one job per place of B: B takes at 2.5, 14.5, 26.5 ({Join(bIn)})");
        Check(ms.Slots.All(sl => sl.GetTimeInState(ElementState.BLOCKED) > 0), "each slot waited BLOCKED before a job");
    }

    static void TestReserveExitRule()
    {
        // A routes by a draw (rand() < 0.5 → B0, a long station, else B1): with the option a job for B0 waits for B0's place
        // even when B1 is free — the rule is evaluated at the start and kept
        var c = Cfg(80);
        Src(c, "src", 1);
        Q(c, "q"); var a = Srv(c, "A", 1); Srv(c, "B0", 10); Srv(c, "B1", 1); Snk(c, "sink");
        a.Set("reserveExit", true);
        a.Set("outputStrategy.strategyType", "Expression");
        a.Set("outputStrategy.routingExpression", "rand() < 0.5 ? 0 : 1");
        Con(c, "src", "q"); Con(c, "q", "A"); Con(c, "A", "B0"); Con(c, "A", "B1"); Con(c, "B0", "sink"); Con(c, "B1", "sink");
        var r = RunCfg(c);
        var ms = (MultiServer)r.model.Find("A");
        int b0 = (int)r.model.Find("B0").GetStatsInputCount(), b1 = (int)r.model.Find("B1").GetStatsInputCount();
        Check(ms.Slots[0].GetTimeInState(ElementState.BLOCKED) > 0, "A waits for B0's place when its job is for B0");
        Check(b0 >= 4 && b1 >= 2, $"both destinations served by the rule ({b0}, {b1})");
        Check(Math.Abs(b0 - b1) <= 3, $"the split stays near 50 / 50 — the draw is kept while it waits ({b0} / {b1})");
        Check(((ExpressionRoutingStrategy)ms.GetOutputStrategy()).Remembered <= 2, "draws forgotten once the items left");
    }

    static void TestReserveExitTransportAndStations()
    {
        // A (reserve) → carried by porters (travel 3) → B (one place, 4): the request takes over A's promise; B counts it once
        var c = Cfg(60);
        Src(c, "src", 1); Q(c, "q"); var a = Srv(c, "A", 1); Srv(c, "B", 4); Snk(c, "sink");
        Con(c, "src", "q"); Con(c, "q", "A"); Con(c, "A", "B"); Con(c, "B", "sink");
        a.Set("reserveExit", true);
        TeamCfg(c, "porters", 2);
        a.Set("transportBy.team", "porters");
        a.Set("transportBy.travelTime", "Constant~3");
        int maxPromised = 0;
        HeadlessModelFactoryBuild(c, out var model, out var clock);
        var b = model.Find("B");
        for (double t = 0.25; t <= 60; t += 0.25) { clock.AdvanceClock(t); maxPromised = Math.Max(maxPromised, b.RoomPromised + b.GetQueueLength()); }
        Check(maxPromised == 1, $"B's place: its item or the one promised, never both ({maxPromised})");
        Check(b.GetStatsInputCount() >= 7, $"items carried ({b.GetStatsInputCount()})");

        // a Combiner reserves the same way (built from the key)
        c = Cfg(12);
        Src(c, "main", 5); Src(c, "comp", 5);
        var comb = new ElementConfig("comb", "Combiner", "comb");
        comb.Set("requirements", new[] { 1 });
        comb.Set("delayStrategy.strategyType", "Constant"); comb.Set("delayStrategy.constantValue", 1.0);
        comb.Set("inputOrder", new List<string> { "main", "comp" });
        comb.Set("reserveExit", true);
        c.Elements.Add(comb); Srv(c, "B", 100); Snk(c, "sink");
        Con(c, "main", "comb"); Con(c, "comp", "comb"); Con(c, "comb", "B"); Con(c, "B", "sink");
        var rc = RunCfg(c);
        var cb = (Combiner)rc.model.Find("comb");
        Check(cb.ReserveExit && rc.model.Find("B").GetStatsInputCount() == 1, "the Combiner's first set goes to B");
        Check(cb.Assemblies[0].Process.GetState() == ElementState.BLOCKED && cb.GetTimeInState(ElementState.PROCESSING) == 1,
              "its second set waits BLOCKED before its process (B busy)");
        bool failed = false;
        c = Cfg(10);
        try { Q(c, "q").Set("reserveExit", true); } catch (ArgumentException e) { failed = e.Message.Contains("reserveExit"); }
        Check(failed, "a queue cannot reserve its exit: writing the key fails naming it");
    }

    // ── securing resources first ──────────────────────────────────────────────────────────────

    static void TestAcquireBeforeEntryWalk()
    {
        // the operator walks 4 from H to the station S (a matrix): with the option the item enters at 104 (it waits in q);
        // without, at 100 and the station shows WAITING_FOR_RESOURCE 100–104
        (double entry, double waiting) Run(bool ahead)
        {
            var c = Cfg(110);
            var mx = new TravelMatrixConfig { Id = "floor" };
            mx.Entries.Add(TravelMatrixEntryConfig.Of("H", "S", 4));
            c.TravelMatrices.Add(mx);
            Src(c, "src", 100);
            Q(c, "q"); var s = Srv(c, "S1", 2); s.Set("location", "S"); Snk(c, "sink");
            Con(c, "src", "q"); Con(c, "q", "S1"); Con(c, "S1", "sink");
            TeamCfg(c, "ops", 1, t => { t.Travel = SimuLean.Serialization.TravelKind.Matrix; t.Matrix = "floor"; t.Speed = 1; t.HomeNode = "H"; });
            s.Set("processBy.team", "ops");
            if (ahead) s.Set("processBy.acquireBeforeEntry", true);
            var r = RunCfg(c, m => m.Find("S1").StatsCollector.RecordSamples());
            var st = r.model.Find("S1");
            return (st.StatsCollector.EntryTimes.Count > 0 ? st.StatsCollector.EntryTimes[0] : double.NaN, st.GetTimeInState(ElementState.WAITING_FOR_RESOURCE));
        }
        var off = Run(false); var on = Run(true);
        Near(off.entry, 100, "without: the part enters at once");
        Near(off.waiting, 4, "without: the station waits 4 for the operator, the part inside");
        Near(on.entry, 104, "with: the part enters when the operator is there");
        Near(on.waiting, 0, "with: never WAITING_FOR_RESOURCE with a part");
    }

    static void TestAcquireBeforeEntryRules()
    {
        // q feeds S1 (option on; its operator busy at S2 1–10.5) and S3 (5 per item): the item at 4 goes to S3 (S1 has no
        // operator; nothing is asked: the item went elsewhere); the item at 8 can go nowhere, so S1 asks for the operator (secured first
        // step 1) and the item goes to S3 at 9; the grant (10.5) waits for the next item, which enters S1 at once (12)
        var c = Cfg(14);
        Src(c, "src", 4);
        Q(c, "q"); var s1 = Srv(c, "S1", 2); Srv(c, "S3", 5); Snk(c, "sink");
        Src(c, "src2", 1);
        var s2 = Srv(c, "S2", 9.5);
        Con(c, "src", "q"); Con(c, "q", "S1"); Con(c, "q", "S3"); Con(c, "S1", "sink"); Con(c, "S3", "sink");
        Con(c, "src2", "S2"); Con(c, "S2", "sink");
        TeamCfg(c, "ops", 1);
        s1.Set("processBy.team", "ops"); s1.Set("processBy.acquireBeforeEntry", true);
        s2.Set("processBy.team", "ops");
        var r = RunCfg(c, m => { m.Find("S1").StatsCollector.RecordSamples(); m.Find("S3").StatsCollector.RecordSamples(); });
        Check(r.model.Find("S3").StatsCollector.EntryTimes.Take(2).SequenceEqual(new double[] { 4, 9 }), $"the items at 4 and 8 went to S3 (S1 had no operator) ({Join(r.model.Find("S3").StatsCollector.EntryTimes)})");
        Check(r.model.Find("S1").StatsCollector.EntryTimes.FirstOrDefault() == 12, $"the next item enters S1 at 12 ({Join(r.model.Find("S1").StatsCollector.EntryTimes)})");
        var op = r.model.Executors.Single();
        Near(op.GetTimeInState(ResourceStates.HELD), 12 - 10.5, "the operator waited HELD at S1 from the grant (10.5) to the item (12)");

        // setups: a setter (own crew) with the process operator secured before the part; and Keep For Process
        foreach (bool keep in new[] { false, true })
        {
            c = Cfg(20);
            Src(c, "src", 5); Q(c, "q"); var s = Srv(c, "S", 2); Snk(c, "sink");
            Con(c, "src", "q"); Con(c, "q", "S"); Con(c, "S", "sink");
            TeamCfg(c, "ops", 1); TeamCfg(c, "setters", 1);
            s.Set("processBy.team", "ops"); s.Set("processBy.acquireBeforeEntry", true);
            s.Set("setup.when", "EachItem"); s.Set("setup.time", "Constant~1");
            s.Set("setup.setupBy.team", keep ? "ops" : "setters");
            if (keep) s.Set("setup.keepForProcess", true);
            r = RunCfg(c, m => m.Find("sink").StatsCollector.RecordSamples());
            var done = r.model.Find("sink").StatsCollector.EntryTimes;
            Check(done.Take(3).SequenceEqual(new double[] { 8, 13, 18 }),
                  $"{(keep ? "kept" : "own crew")}: each part enters at its arrival (secured), 1 of setup, 2 of process ({Join(done)})");
            var ops = r.model.Executors.First(x => x.Name == "ops1");
            Near(ops.GetTimeInState(ElementState.SETUP), keep ? 3 : 0, $"{(keep ? "kept" : "own crew")}: the operator sets up only when the setup is kept for the process");
        }

        // config: an Assembly Station refuses the key
        c = Cfg(10);
        Src(c, "src", 1); var asy = new ElementConfig("asy", "AssemblyStation", "asy"); asy.Set("tasks", new List<AssemblyTaskSettings> { AssemblyTaskSettings.Of("a", SamplerSettings.Constant(1), 0) });
        c.Elements.Add(asy); Snk(c, "sink"); Con(c, "src", "asy"); Con(c, "asy", "sink");
        TeamCfg(c, "ops", 1);
        asy.Set("processBy.team", "ops"); asy.Set("processBy.acquireBeforeEntry", true);
        bool failed = false;
        try { HeadlessModelFactoryBuild(c, out _, out _); } catch (FormatException e) { failed = e.Message.Contains("acquireBeforeEntry"); }
        Check(failed, "an Assembly Station cannot secure its resources before the part (per task): the build fails");
    }
}
