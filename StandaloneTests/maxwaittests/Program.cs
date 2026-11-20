// Standalone tests of the max-wait timers: any wait that carries a limit (`maxWait`: a time or an expression
// evaluated when the wait starts) and what it does when the limit is over (`onTimeout`: KeepWaiting, GiveUp, Reroute) —
// a station's resource requests (in order, secured before the part, re-asked behind the others, an item rerouted
// unprocessed), a Queue's items (reneging to a timeout target; a full target; downtime), a list puller; the timer
// cancelled on a normal end, a reset and a preemption; the signal WaitTimedOut caught by a reaction; the `timeouts`
// statistics; seeded reproducibility; the config keys and their errors. Defaults keep every model unchanged (the
// reference baselines guard that); the reference model `max_wait` combines queue reneging and an operator released by
// GiveUp.
// Run: dotnet run  (from StandaloneTests/maxwaittests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Results;
using SimuLean.Serialization;
using SimuLean.Signals;

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
        Run("Resource, KeepWaiting: the timeout is counted and signalled once; the job goes on and is served", TestResourceKeepWaiting);
        Run("Resource: a job served before its max wait never times out (the timer cancelled at the grant)", TestResourceNormalEnd);
        Run("Resource, GiveUp in order: the crane held for the driver is released to another station; the job asks again", TestInOrderGiveUp);
        Run("Resource, GiveUp before the part: the operator HELD for an item that never comes is released", TestBeforeEntryGiveUp);
        Run("Resource, GiveUp with the item inside: the job asks again behind the requests made meanwhile", TestGiveUpAsksAgainBehind);
        Run("Resource, Reroute (Multi-Server): the item leaves unprocessed by the timeout target; a full target keeps it", TestRerouteMultiServer);
        Run("Queue, Reroute: items that waited too long leave by the timeout target (link statistics, counts)", TestQueueReneging);
        Run("Queue, KeepWaiting: counted and signalled, nothing leaves", TestQueueKeepWaiting);
        Run("Queue, a full timeout target: the item keeps its place in the line and goes when the target has room", TestQueueTargetFull);
        Run("Queue: downtime blocks the reroute; it goes when the queue resumes", TestQueueDowntime);
        Run("Timers cancelled on a reset (queue, station, team) and on a withdrawn job", TestReset);
        Run("Preemption: a member held for the job taken by higher work does not restart or double the timer", TestPreemption);
        Run("Signal: WaitTimedOut caught by a reaction (signal.kind, signal.waited); item label; results", TestSignalReaction);
        Run("Statistics: timeouts and timeouts:<kind> only for elements with a limit", TestMetrics);
        Run("Seeded reproducibility: a random max wait draws from its own stream", TestSeeded);
        Run("List pull, KeepWaiting: the puller's wait from its pull to a match; reset", TestListPull);
        Run("Config: keys of every place, the catalog, and errors", TestConfig);
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

    sealed class Up : Element
    {
        public Up(SimClock c, string n) : base(n, c) { }
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
        public override bool Unblock() => false;
    }

    sealed class Signals : ISignalSink
    {
        public readonly List<SignalArgs> Got = new List<SignalArgs>();
        public void OnSignal(in SignalArgs s) => Got.Add(s);
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

    static Element SinkOf(MultiServer s) => ((GeneralLink)s.GetOutput()).Outputs[0];
    static double LastEntry(Element e) => e.StatsCollector.LastEntryTime;

    static SimulationConfig Cfg(double horizon = 200, int seed = 1) => new SimulationConfig { UseFixedSeed = true, Seed = seed, MaxSimulationTime = horizon };
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
    static void TeamCfg(SimulationConfig c, string id, int n)
    {
        var tc = new ResourceTeamConfig { Id = id, Name = id };
        for (int i = 0; i < n; i++) tc.Members.Add(new ResourceMemberConfig { Name = id + (i + 1) });
        c.ResourceTeams.Add(tc);
    }
    static SimuLean.Headless.HeadlessModelFactory Build(SimulationConfig c, out SimClock clock)
    {
        clock = new SimClock(c.Seed);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        return model;
    }
    static SimuLean.Headless.HeadlessModelFactory RunCfg(SimulationConfig c, Action<SimuLean.Headless.HeadlessModelFactory> before = null)
    {
        var model = Build(c, out var clock);
        before?.Invoke(model);
        var old = SimLog.WarningSink; SimLog.WarningSink = w => { };
        try { clock.AdvanceClock(c.MaxSimulationTime); } finally { SimLog.WarningSink = old; }
        return model;
    }
    static string Join(IEnumerable<double> v) => string.Join(", ", v ?? new List<double>());
    static bool Fails(SimulationConfig c, string what)
    {
        try { Build(c, out _); return false; }
        catch (Exception e) when (e is FormatException || e is ArgumentException) { return e.Message.Contains(what); }
    }

    // ── resource requests ────────────────────────────────────────────────

    // H holds the only operator 0–20; S's job (item at 1) waits: its max wait 5 is over at 6 — counted, signalled once.
    static void TestResourceKeepWaiting()
    {
        var clock = new SimClock(1);
        var (ops, o) = Team(clock, "Op", 1);
        var h = Station(clock, "H", 20, new StationResources(ops));
        var s = Station(clock, "S", 2, new StationResources(ops) { MaxWait = new WaitLimit(5, TimeoutAction.KeepWaiting) });
        var sig = new Signals();
        clock.Context.Signals.Subscribe(s, SignalKind.WaitTimedOut, sig, SignalDelivery.Immediate);
        StartAll(new[] { ops }, h, s, SinkOf(h), SinkOf(s));
        h.Receive(new Item(0));
        var item = new Item(1);
        Schedule(clock, 1, () => s.Receive(item));
        int at7 = -1; ElementState stateAt7 = default;
        Schedule(clock, 7, () => { at7 = s.Timeouts; stateAt7 = s.CurrentState; });
        clock.AdvanceClock(60);
        Check(at7 == 1, $"one timeout at 6 ({at7})");
        Check(stateAt7 == ElementState.WAITING_FOR_RESOURCE, "the job keeps waiting");
        Near(LastEntry(SinkOf(s)), 22, "served when the operator is free (20–22)");
        Check(s.Timeouts == 1 && s.TimeoutsOf(WaitKind.Resource) == 1, "one timeout in all (never again for this wait)");
        Check(sig.Got.Count == 1, $"one signal ({sig.Got.Count})");
        if (sig.Got.Count == 1)
        {
            var g = sig.Got[0];
            Check(g.Kind == SignalKind.WaitTimedOut && g.Source == s && g.Item == item, "the signal: its element and item");
            Near(g.Waited, 5, "signal: the time waited");
            Near(g.Time, 6, "signal: the time");
            Check(g.WaitKind == "resource" && g.WaitAction == "KeepWaiting", $"signal: kind and action ({g.WaitKind}, {g.WaitAction})");
        }
    }

    static void TestResourceNormalEnd()
    {
        var clock = new SimClock(1);
        var (ops, o) = Team(clock, "Op", 1);
        var h = Station(clock, "H", 20, new StationResources(ops));
        var s = Station(clock, "S", 2, new StationResources(ops) { MaxWait = new WaitLimit(30, TimeoutAction.GiveUp) });
        StartAll(new[] { ops }, h, s, SinkOf(h), SinkOf(s));
        h.Receive(new Item(0));
        Schedule(clock, 1, () => s.Receive(new Item(1)));
        Schedule(clock, 25, () => s.Receive(new Item(2)));   // served at once: no wait, no timer
        clock.AdvanceClock(100);
        Check(s.Timeouts == 0, "no timeout: served at 20, before its max wait (31)");
        Near(LastEntry(SinkOf(s)), 27, "both items served (20–22, 25–27)");
        Check(s.GetStatsOutputCount() == 2, "two items out");
    }

    // The crane (in order: crane, then a driver busy 0–20) is held from 1; Hook needs the crane at 2. Max wait 5, GiveUp:
    // at 6 the crane is released — Hook takes it (6–9) — and the lift asks again (crane at 9, given up at 11, at 16), the
    // driver comes at 20: the lift runs 20–25.
    static void TestInOrderGiveUp()
    {
        var clock = new SimClock(1);
        var (cranes, c) = Team(clock, "Crane", 1);
        var (drivers, dr) = Team(clock, "Driver", 1);
        var use = new StationResources(cranes, new ResourceRequirement(), new ResourceRequirement(1, null, "driver", drivers))
        { Order = AcquireOrder.InOrder, MaxWait = new WaitLimit(5, TimeoutAction.GiveUp) };
        var lift = Station(clock, "Lift", 5, use);
        var drive = Station(clock, "Drive", 20, new StationResources(drivers));
        var hook = Station(clock, "Hook", 3, new StationResources(cranes));
        StartAll(new[] { cranes, drivers }, lift, drive, hook, SinkOf(lift), SinkOf(drive), SinkOf(hook));
        drive.Receive(new Item(0));
        Schedule(clock, 1, () => lift.Receive(new Item(1)));
        Schedule(clock, 2, () => hook.Receive(new Item(2)));
        clock.AdvanceClock(60);
        Near(LastEntry(SinkOf(hook)), 9, "Hook gets the crane given up at 6 (6–9)");
        Near(LastEntry(SinkOf(lift)), 25, "the lift runs when the driver comes (20–25)");
        Check(lift.Timeouts == 3, $"three timeouts (6, 11, 16) ({lift.Timeouts})");
        Near(c[0].GetTimeInState(ResourceStates.HELD), 16, "the crane HELD 1–6, 9–11, 11–16, 16–20");
        Check(cranes.Requests.Count == 0 && drivers.Requests.Count == 0, "nothing pending at the end");

        // without a max wait: Hook waits for the crane until the lift is done (25–28)
        clock = new SimClock(1);
        (cranes, c) = Team(clock, "Crane", 1);
        (drivers, dr) = Team(clock, "Driver", 1);
        lift = Station(clock, "Lift", 5, new StationResources(cranes, new ResourceRequirement(), new ResourceRequirement(1, null, "driver", drivers)) { Order = AcquireOrder.InOrder });
        drive = Station(clock, "Drive", 20, new StationResources(drivers));
        hook = Station(clock, "Hook", 3, new StationResources(cranes));
        StartAll(new[] { cranes, drivers }, lift, drive, hook, SinkOf(lift), SinkOf(drive), SinkOf(hook));
        drive.Receive(new Item(0));
        Schedule(clock, 1, () => lift.Receive(new Item(1)));
        Schedule(clock, 2, () => hook.Receive(new Item(2)));
        clock.AdvanceClock(60);
        Near(LastEntry(SinkOf(hook)), 28, "without a limit Hook waits for the held crane (25–28)");
    }

    // Securing resources first: q feeds S (before the part, the operator busy at H 0–10) and T (busy 0–4 with an item of its own). The item at 1
    // can go nowhere: S, which admits it, asks for the operator (step 1, at 1); at 4 it goes to T. The grant comes at 10
    // and waits HELD for an item that never comes: GiveUp at 13 (max wait 12 from the request at 1) frees the operator; H's
    // second job (15) is served at once. KeepWaiting: the operator stays HELD, H's second job never starts.
    static void TestBeforeEntryGiveUp()
    {
        foreach (var action in new[] { TimeoutAction.GiveUp, TimeoutAction.KeepWaiting })
        {
            var clock = new SimClock(1);
            var (ops, o) = Team(clock, "Op", 1);
            var h = Station(clock, "H", 10, new StationResources(ops));
            var q = new ItemsQueue(10, "q", clock);
            var s = new MultiServer(new IRandomSampler[] { C(2) }, "S", clock)
            { Resources = new StationResources(ops) { AcquireBeforeEntry = true, MaxWait = new WaitLimit(12, action) } };
            var t = new MultiServer(new IRandomSampler[] { C(4) }, "T", clock);
            Link(new Up(clock, "UpQ"), q);
            GeneralLink.CreateLink(q, new List<Element> { s, t });
            var sinkS = new Sink("sinkS", clock); var sinkT = new Sink("sinkT", clock);
            Link(s, sinkS); Link(t, sinkT);
            StartAll(new[] { ops }, h, q, s, t, sinkS, sinkT, SinkOf(h));
            h.Receive(new Item(0));
            t.Receive(new Item(9));
            Schedule(clock, 1, () => q.Receive(new Item(1)));
            Schedule(clock, 15, () => h.Receive(new Item(2)));
            clock.AdvanceClock(60);
            Check(t.GetStatsInputCount() == 1 && s.GetStatsInputCount() == 0, $"{action}: the item went to T, none to S");
            Check(s.Timeouts == 1, $"{action}: one timeout at 13 ({s.Timeouts})");
            if (action == TimeoutAction.GiveUp)
            {
                Near(o[0].GetTimeInState(ResourceStates.HELD), 3, "GiveUp: the operator HELD at S only 10–13");
                Near(LastEntry(SinkOf(h)), 25, "GiveUp: H's second job is served at once (15–25)");
                Check(s.Resources.GrantedAhead == 0, "GiveUp: nothing secured any more");
                Check(o[0].IsAvailable, "GiveUp: the operator is free at the end");
                Check(o[0].TasksDone == 2, $"GiveUp: the withdrawn task is not a completed one — only H's two ({o[0].TasksDone})");
            }
            else
            {
                Check(SinkOf(h).GetStatsInputCount() == 1, "KeepWaiting: H's second job never gets the operator HELD at S");
                Near(o[0].GetTimeInState(ResourceStates.HELD), 50, "KeepWaiting: the operator HELD from 10 to the end");
            }
        }
    }

    // H holds the operator 0–10. S (item at 1, max wait 4, GiveUp) gives up at 5 and 9, asking again each time; S2 asks at
    // 6. At 10 S2 (asked before S's last request) is served first (10–11), then S (11–13). KeepWaiting: S first (10–12).
    static void TestGiveUpAsksAgainBehind()
    {
        foreach (var action in new[] { TimeoutAction.GiveUp, TimeoutAction.KeepWaiting })
        {
            var clock = new SimClock(1);
            var (ops, o) = Team(clock, "Op", 1);
            var h = Station(clock, "H", 10, new StationResources(ops));
            var s = Station(clock, "S", 2, new StationResources(ops) { MaxWait = new WaitLimit(4, action) });
            var s2 = Station(clock, "S2", 1, new StationResources(ops));
            StartAll(new[] { ops }, h, s, s2, SinkOf(h), SinkOf(s), SinkOf(s2));
            h.Receive(new Item(0));
            Schedule(clock, 1, () => s.Receive(new Item(1)));
            Schedule(clock, 6, () => s2.Receive(new Item(2)));
            clock.AdvanceClock(40);
            if (action == TimeoutAction.GiveUp)
            {
                Check(s.Timeouts == 2, $"GiveUp: two timeouts (5, 9) ({s.Timeouts})");
                Near(LastEntry(SinkOf(s2)), 11, "GiveUp: S2 first (10–11)");
                Near(LastEntry(SinkOf(s)), 13, "GiveUp: S behind it (11–13)");
                Check(ops.Requests.Count == 0, "GiveUp: no request left behind (the withdrawn ones cancelled)");
                Check(ops.Served == 3, $"GiveUp: three jobs served, the withdrawn requests not counted ({ops.Served})");
            }
            else
            {
                Check(s.Timeouts == 1, "KeepWaiting: one timeout (5)");
                Near(LastEntry(SinkOf(s)), 12, "KeepWaiting: S first (10–12)");
                Near(LastEntry(SinkOf(s2)), 13, "KeepWaiting: S2 behind (12–13)");
            }
        }
    }

    // H holds the operator 1–101; S's items (every 10) give up after 3 and leave by 'lost' unprocessed.
    static SimulationConfig RerouteModel(bool fullTarget)
    {
        var c = Cfg(58);
        Src(c, "srcH", 1); Srv(c, "H", 100); Src(c, "src", 10);
        var s = Srv(c, "S", 2); Snk(c, "sink");
        if (fullTarget) Srv(c, "lost", 1000); else Snk(c, "lost");
        Con(c, "srcH", "H"); Con(c, "H", "sink"); Con(c, "src", "S"); Con(c, "S", "sink");
        TeamCfg(c, "ops", 1);
        c.Elements.Find(x => x.Id == "H").Set("processBy.team", "ops");
        s.Set("processBy.team", "ops");
        s.Set("processBy.maxWait", "3");
        s.Set("processBy.onTimeout", "Reroute");
        s.Set("processBy.timeoutTarget", "lost");
        return c;
    }

    static void TestRerouteMultiServer()
    {
        var m = RunCfg(RerouteModel(false), x => x.Find("lost").StatsCollector.RecordSamples());
        var s = (MultiServer)m.Find("S");
        var lost = m.Find("lost");
        Check(lost.StatsCollector.EntryTimes.SequenceEqual(new double[] { 13, 23, 33, 43, 53 }), $"each item leaves 3 after its arrival ({Join(lost.StatsCollector.EntryTimes)})");
        Check(s.GetStatsOutputCount() == 5 && s.GetStatsInputCount() == 5, "the station's link statistics count them (5 in, 5 out)");
        Check(s.Timeouts == 5 && s.TimeoutTarget == lost, "five timeouts; the target wired");
        Check(m.Find("sink").GetStatsInputCount() == 0, "none processed (the operator never came)");
        Check(m.Executors.Single().TasksDone == 0 && m.Executors.Single().CurrentTask != null, "the operator only worked at H");

        // a full target: the second item waits in its slot (BLOCKED) for it
        m = RunCfg(RerouteModel(true));
        s = (MultiServer)m.Find("S");
        Check(m.Find("lost").GetStatsInputCount() == 1, "the target (one place, busy) takes one item");
        Check(s.Timeouts == 2 && s.GetQueueLength() == 1 && s.CurrentState == ElementState.BLOCKED,
              $"the second item waits for the target in its slot, BLOCKED ({s.Timeouts}, {s.GetQueueLength()}, {s.CurrentState.Name})");
    }

    // ── queue reneging ───────────────────────────────────────────────────

    // src every 1 → q (max wait 5.5) → S (3) → sink; the items that would wait more than 5.5 leave by 'lost'.
    static SimulationConfig QueueModel(string action, string maxWait = "5.5", bool fullTarget = false, double horizon = 30, int seed = 1)
    {
        var c = Cfg(horizon, seed);
        Src(c, "src", 1); var q = Q(c, "q"); Srv(c, "S", 3); Snk(c, "sink");
        if (fullTarget) Srv(c, "lost", 1000); else Snk(c, "lost");
        Con(c, "src", "q"); Con(c, "q", "S"); Con(c, "S", "sink");
        q.Set("maxWait", maxWait);
        q.Set("onTimeout", action);
        q.Set("timeoutTarget", "lost");
        return c;
    }

    static void TestQueueReneging()
    {
        foreach (var action in new[] { "Reroute", "GiveUp" })
        {
            var m = RunCfg(QueueModel(action), x => x.Find("lost").StatsCollector.RecordSamples());
            var q = (ItemsQueue)m.Find("q");
            var lost = m.Find("lost");
            Near(lost.StatsCollector.EntryTimes.FirstOrDefault(), 9.5, $"{action}: the first item lost is the 4th (arrived at 4, its turn at 10)");
            Check(q.GetStatsStayTimeMax() <= 5.5 + 1e-9, $"{action}: no item waits more than 5.5 ({q.GetStatsStayTimeMax()})");
            Check(q.GetStatsInputCount() == q.GetStatsOutputCount() + q.GetQueueLength(), $"{action}: the queue's flow balances");
            Check(q.GetStatsOutputCount() == m.Find("S").GetStatsInputCount() + lost.GetStatsInputCount(), $"{action}: every item out went to S or to the target");
            Check(lost.GetStatsInputCount() == q.TimeoutsOf(WaitKind.Queue) && q.Timeouts > 3, $"{action}: every timeout lost an item ({q.Timeouts})");
            Check(q.Reneging == 0, $"{action}: none waiting for the target");
        }
    }

    static void TestQueueKeepWaiting()
    {
        var m = RunCfg(QueueModel("KeepWaiting"));
        var q = (ItemsQueue)m.Find("q");
        Check(m.Find("lost").GetStatsInputCount() == 0, "nothing goes to the target");
        Check(q.Timeouts > 3 && q.GetStatsStayTimeMax() > 5.5, $"counted, and items wait longer ({q.Timeouts}, {q.GetStatsStayTimeMax()})");
        // no target needed for KeepWaiting
        var c = QueueModel("KeepWaiting"); c.Elements.Find(x => x.Id == "q").Remove("timeoutTarget");
        Check(RunCfg(c).Find("q").Timeouts == q.Timeouts, "KeepWaiting needs no target");
    }

    static void TestQueueTargetFull()
    {
        var m = RunCfg(QueueModel("Reroute", fullTarget: true));
        var q = (ItemsQueue)m.Find("q");
        Check(m.Find("lost").GetStatsInputCount() == 1, "the busy target takes one item");
        Check(q.Timeouts > 1, $"later items time out too ({q.Timeouts})");
        Check(q.GetStatsStayTimeMax() > 5.5, "an item waiting for the target kept its place and left normally when its turn came");
        Check(q.Reneging > 0 && q.Reneging <= q.GetQueueLength(), $"items waiting for the target are still in the line ({q.Reneging})");
    }

    // q → S (100); the item queued at 1 times out at 3 while q is stopped (2–6): it leaves by 'lost' at the resume.
    static void TestQueueDowntime()
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(10, "q", clock);
        var s = new MultiServer(new IRandomSampler[] { C(100) }, "S", clock);
        var sink = new Sink("sink", clock); var lost = new Sink("lost", clock);
        Link(new Up(clock, "Up"), q); Link(q, s); Link(s, sink);
        q.MaxWait = new WaitLimit(2, TimeoutAction.Reroute); q.SetTimeoutTarget(lost); q.DeclareWaitLimit(WaitKind.Queue);
        foreach (var e in new Element[] { q, s, sink, lost }) e.Start();
        q.Receive(new Item(0));
        Schedule(clock, 1, () => q.Receive(new Item(1)));
        StopToken token = null;
        Schedule(clock, 2, () => token = q.Stop(new StopRequest { BlockOutput = true }));
        int renegingAt4 = -1;
        Schedule(clock, 4, () => renegingAt4 = q.Reneging);
        Schedule(clock, 6, () => q.Resume(token));
        clock.AdvanceClock(20);
        Check(renegingAt4 == 1 && q.Timeouts == 1, $"timed out at 3, held by the stop ({renegingAt4})");
        Near(LastEntry(lost), 6, "it leaves by the target when the queue resumes");
        Check(q.GetQueueLength() == 0 && lost.GetStatsInputCount() == 1, "one item lost, the queue empty");

        // the target stopped: it goes when the target resumes
        clock = new SimClock(1);
        q = new ItemsQueue(10, "q", clock);
        s = new MultiServer(new IRandomSampler[] { C(100) }, "S", clock);
        sink = new Sink("sink", clock); lost = new Sink("lost", clock);
        Link(new Up(clock, "Up"), q); Link(q, s); Link(s, sink);
        q.MaxWait = new WaitLimit(2, TimeoutAction.Reroute); q.SetTimeoutTarget(lost);
        foreach (var e in new Element[] { q, s, sink, lost }) e.Start();
        q.Receive(new Item(0));
        Schedule(clock, 1, () => q.Receive(new Item(1)));
        StopToken t2 = null;
        Schedule(clock, 2, () => t2 = lost.Stop(new StopRequest()));
        Schedule(clock, 8, () => lost.Resume(t2));
        clock.AdvanceClock(20);
        Near(LastEntry(lost), 8, "a stopped target takes it when it resumes");
    }

    // ── cancellation ─────────────────────────────────────────────────────

    static void TestReset()
    {
        // a queue: its Start (a new run) cancels the timers of the items it held
        var clock = new SimClock(1);
        var q = new ItemsQueue(10, "q", clock);
        var s = new MultiServer(new IRandomSampler[] { C(100) }, "S", clock);
        Link(new Up(clock, "Up"), q); Link(q, s); Link(s, new Sink("sink", clock));
        q.MaxWait = new WaitLimit(5, TimeoutAction.KeepWaiting);
        q.Start(); s.Start();
        q.Receive(new Item(0)); q.Receive(new Item(1));
        Schedule(clock, 2, () => q.Start());
        clock.AdvanceClock(20);
        Check(q.Timeouts == 0, "queue: no timeout after its reset");

        // a station's job: the team's Start (a new run) cancels it; Cancel(job) too
        foreach (bool cancelJob in new[] { false, true })
        {
            clock = new SimClock(1);
            var (ops, o) = Team(clock, "Op", 1);
            var h = Station(clock, "H", 50, new StationResources(ops));
            var st = Station(clock, "S", 2, new StationResources(ops) { MaxWait = new WaitLimit(5, TimeoutAction.KeepWaiting) });
            StartAll(new[] { ops }, h, st, SinkOf(h), SinkOf(st));
            h.Receive(new Item(0));
            Schedule(clock, 1, () => st.Receive(new Item(1)));
            if (cancelJob) Schedule(clock, 2, () => st.Resources.Cancel(st.Slots[0].job));
            else Schedule(clock, 2, () => ops.Start());
            clock.AdvanceClock(20);
            Check(st.Timeouts == 0, cancelJob ? "a cancelled job: no timeout" : "station: no timeout after its team's reset");
        }
    }

    // In order, crane then a driver busy 0–30; the crane held from 1 is taken by an urgent job (priority 5, 3–5) and comes
    // back: one wait, one timer (from 1) — KeepWaiting: one timeout at 11; GiveUp: 11 and 21.
    static void TestPreemption()
    {
        foreach (var action in new[] { TimeoutAction.KeepWaiting, TimeoutAction.GiveUp })
        {
            var clock = new SimClock(1);
            var (cranes, c) = Team(clock, "Crane", 1);
            var (drivers, dr) = Team(clock, "Driver", 1);
            var lift = Station(clock, "Lift", 5, new StationResources(cranes, new ResourceRequirement(), new ResourceRequirement(1, null, "driver", drivers))
            { Order = AcquireOrder.InOrder, MaxWait = new WaitLimit(10, action) });
            var drive = Station(clock, "Drive", 30, new StationResources(drivers));
            var urgent = Station(clock, "U", 2, new StationResources(cranes) { Priority = _ => 5, Preempt = PreemptMode.Resume });
            StartAll(new[] { cranes, drivers }, lift, drive, urgent, SinkOf(lift), SinkOf(drive), SinkOf(urgent));
            drive.Receive(new Item(0));
            Schedule(clock, 1, () => lift.Receive(new Item(1)));
            Schedule(clock, 3, () => urgent.Receive(new Item(2)));
            clock.AdvanceClock(60);
            Near(LastEntry(SinkOf(urgent)), 5, $"{action}: the urgent job takes the held crane (3–5)");
            Check(c[0].Preemptions == 1, $"{action}: one preemption");
            Check(lift.Timeouts == (action == TimeoutAction.KeepWaiting ? 1 : 2), $"{action}: the timer neither restarted nor doubled by the preemption ({lift.Timeouts})");
            Near(LastEntry(SinkOf(lift)), 35, $"{action}: the lift runs when the driver comes (30–35)");
        }
    }

    // ── signal, statistics, seeds ────────────────────────────────────────

    static void TestSignalReaction()
    {
        var c = QueueModel("Reroute");
        var r = new ReactionConfig("renege", "q");
        r.Set("signal", "WaitTimedOut");
        r.Set("condition", "signal.kind == 'queue' && signal.action == 'Reroute'");
        r.AddAction("SetElementProperty", target: "q", name: "lastWaited", value: "signal.waited");
        r.AddAction("SetItemLabel", name: "reneged", value: "1");
        c.Reactions.Add(r);
        var m = RunCfg(c);
        var q = m.Find("q");
        var reaction = m.ReactionsById["renege"];
        Check(reaction.Fired == q.Timeouts && q.Timeouts > 0, $"the reaction fired on every timeout ({reaction.Fired} / {q.Timeouts})");
        Check(q.Properties.TryGetDouble("lastWaited", out double w) && Math.Abs(w - 5.5) < 1e-9, "signal.waited = the max wait (5.5)");
        var rr = RunResults.Capture(m);
        Check(rr.Find(ResultScope.Reaction, "renege")[ResultMetrics.Fired] == q.Timeouts, "its firings in the results");

        // an item label set on the timed-out item (immediate delivery: before it leaves)
        c = QueueModel("Reroute");
        r = new ReactionConfig("mark", "q");
        r.Set("signal", "WaitTimedOut");
        r.Set("timing", "Immediate");
        r.AddAction("SetItemLabel", name: "reneged", value: "1");
        c.Reactions.Add(r);
        var marked = 0;
        m = RunCfg(c, x => x.Find("lost").ItemEntered += (e, it) => { if (it.Properties.TryGetDouble("reneged", out double v) && v == 1) marked++; });
        Check(marked == m.Find("lost").GetStatsInputCount() && marked > 0, $"every lost item carries the label ({marked})");
    }

    static void TestMetrics()
    {
        var m = RunCfg(RerouteModel(false));
        var rr = RunResults.Capture(m);
        var s = rr.Find(ResultScope.Element, "S");
        Check(s.TryGet(ResultMetrics.Timeouts, out double t) && t == 5, "timeouts of the station");
        Check(s.TryGet(ResultMetrics.TimeoutsOf(WaitKind.Resource), out double tr) && tr == 5, "timeouts:resource");
        Check(!s.TryGet(ResultMetrics.TimeoutsOf(WaitKind.Queue), out _), "no timeouts:queue (not limited)");
        Check(!rr.Find(ResultScope.Element, "H").TryGet(ResultMetrics.Timeouts, out _), "an element without a limit has no timeouts");
        Check(ResultMetrics.TimeoutsOf(WaitKind.ListPull) == "timeouts:list_pull", "the per-kind names");
        var qm = RunCfg(QueueModel("Reroute"));
        var qr = RunResults.Capture(qm).Find(ResultScope.Element, "q");
        Check(qr.TryGet(ResultMetrics.TimeoutsOf(WaitKind.Queue), out double tq) && tq == qm.Find("q").Timeouts, "timeouts:queue");

        // the warm-up forgets the timeouts before it
        var c = QueueModel("Reroute"); c.WarmupTime = 15;
        var w = RunCfg(c);
        Check(w.Find("q").Timeouts < qm.Find("q").Timeouts && w.Find("q").Timeouts > 0, "the warm-up resets the count");
    }

    static void TestSeeded()
    {
        (int lost, double stay) Once(int seed)
        {
            var m = RunCfg(QueueModel("Reroute", "uniform(2, 8)", horizon: 200, seed: seed));
            return ((int)m.Find("lost").GetStatsInputCount(), m.Find("q").GetStatsStayTimeMean());
        }
        var a = Once(7); var b = Once(7); var d = Once(8);
        Check(a == b, $"same seed, same run ({a} / {b})");
        Check(a != d, $"another seed draws other limits ({a} / {d})");
        Check(a.lost > 0, "some items lost");
    }

    // ── list pull ────────────────────────────────────────────────────────

    // src (every 10) pushes to a list; S pulls with a max wait of 4: waits 0–10, 12–20, 22–30 → timeouts at 4, 16, 26.
    static SimulationConfig PullModel()
    {
        var c = Cfg(35);
        Src(c, "src", 10); var s = Srv(c, "S", 2); Snk(c, "sink");
        c.Lists.Add(new ListConfig { Id = "L", Name = "L" });
        c.Connections.Add(new ConnectionConfig("src", "L", "PushToList"));
        var pull = new ConnectionConfig("L", "S", "PullFromList"); pull.Query = ""; c.Connections.Add(pull);
        Con(c, "S", "sink");
        s.Set("pullMaxWait", "4");
        return c;
    }

    static void TestListPull()
    {
        var m = RunCfg(PullModel());
        var s = m.Find("S");
        Check(s.Timeouts == 3 && s.TimeoutsOf(WaitKind.ListPull) == 3, $"three timeouts ({s.Timeouts})");
        Check(m.Find("sink").GetStatsInputCount() == 3, "every item pulled all the same (KeepWaiting)");
        var rr = RunResults.Capture(m).Find(ResultScope.Element, "S");
        Check(rr.TryGet(ResultMetrics.TimeoutsOf(WaitKind.ListPull), out double t) && t == 3, "timeouts:list_pull");

        // a new run (the model started again at 2): the first pull's timer (4) is cancelled, the new one runs to 6
        var c = PullModel();
        var model = Build(c, out var clock);
        clock.AdvanceClock(2);
        model.StartModel();
        clock.AdvanceClock(5);
        Check(model.Find("S").Timeouts == 0, "reset: the old timer never fires");
        clock.AdvanceClock(7);
        Check(model.Find("S").Timeouts == 1, "the new wait times out at 6");

        // an element that pulls from no list refuses the key; GiveUp is not a pull's action
        c = Cfg(10); Src(c, "src", 1); var s2 = Srv(c, "S", 1); Snk(c, "sink"); Con(c, "src", "S"); Con(c, "S", "sink");
        s2.Set("pullMaxWait", "4");
        Check(Fails(c, "pullMaxWait"), "pullMaxWait without a list fails the build");
        bool threw = false;
        try { new ListLink(new SimuLean.Collections.SimList("x", new SimClock(1))).SetPullLimit(new Sink("k", new SimClock(1)), new WaitLimit(1, TimeoutAction.GiveUp)); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "a pull can only keep waiting");
    }

    // ── config ───────────────────────────────────────────────────────────

    static void TestConfig()
    {
        // process: constant, expression, action
        var c = RerouteModel(false);
        var m = Build(c, out _);
        var use = ((MultiServer)m.Find("S")).Resources;
        Check(use.MaxWait != null && use.MaxWait.OnTimeout == TimeoutAction.Reroute && use.MaxWait.Text == "3", "processBy.maxWait / onTimeout built");
        Check(m.Find("S").LimitedWaits.Contains(WaitKind.Resource), "the station reports its resource timeouts");
        c = RerouteModel(false); c.Elements.Find(x => x.Id == "S").Set("processBy.maxWait", "item.id * 0 + 3");
        m = Build(c, out _);
        Check(((MultiServer)m.Find("S")).Resources.MaxWait.Text == "item.id * 0 + 3", "an expression");
        c = RerouteModel(false); c.Elements.Find(x => x.Id == "S").Set("processBy.maxWait", "0");
        m = Build(c, out _);
        Check(((MultiServer)m.Find("S")).Resources.MaxWait == null && m.Find("S").LimitedWaits.Count == 0, "0 = no limit");

        // setup crew, repair crew, an assembly station's tasks
        c = Cfg(20); Src(c, "src", 5); var st = Srv(c, "S", 1); Snk(c, "sink"); Con(c, "src", "S"); Con(c, "S", "sink");
        TeamCfg(c, "setters", 1); TeamCfg(c, "mech", 1);
        st.Set("setup.when", "EachItem"); st.Set("setup.time", "Constant~1"); st.Set("setup.setupBy.team", "setters");
        st.Set("setup.setupBy.maxWait", "2"); st.Set("setup.setupBy.onTimeout", "GiveUp");
        var fail = new DowntimeConfig("fail", "MtbfMttr", "S");
        fail.Set("timeToFailure", "Constant~7"); fail.Set("timeToRepair", "Constant~1");
        fail.Set("repairBy.team", "mech"); fail.Set("repairBy.maxWait", "4");
        c.Downtimes.Add(fail);
        m = RunCfg(c);
        var ms = (MultiServer)m.Find("S");
        Check(ms.Setup.Resources.MaxWait != null && ms.Setup.Resources.MaxWait.OnTimeout == TimeoutAction.GiveUp, "setup.setupBy.maxWait / onTimeout");
        Check(m.DowntimeGenerators.Single().Repair.Crew.MaxWait != null, "repairBy.maxWait");
        Check(m.Find("sink").GetStatsInputCount() > 0, "the model runs");
        var asy = new ElementConfig("asy", "AssemblyStation", "asy"); asy.Set("tasks", new List<AssemblyTaskSettings> { AssemblyTaskSettings.Of("a", SamplerSettings.Constant(1), 0) });
        c = Cfg(10); Src(c, "src", 1); c.Elements.Add(asy); Snk(c, "sink"); Con(c, "src", "asy"); Con(c, "asy", "sink"); TeamCfg(c, "ops", 1);
        asy.Set("processBy.team", "ops"); asy.Set("processBy.maxWait", "2");
        m = Build(c, out _);
        Check(((AssemblyStation)m.Find("asy")).Resources.MaxWait != null, "the assembly station's use (its tasks ask with it)");

        // the catalog decides every key (a number while constant, the action a choice)
        var rc = RerouteModel(false);
        var cat = SimuLean.Optimization.ModelParameterCatalog.ForElement(rc.Elements.Find(x => x.Id == "S"));
        Check(cat.Any(p => p.Key == "processBy.maxWait"), "processBy.maxWait is a catalog number while constant");
        Check(cat.Any(p => p.Key == "processBy.onTimeout" && p.Options.Contains("Reroute")), "processBy.onTimeout is a catalog choice");
        Check(SimuLean.Optimization.ModelParameterCatalog.Undecided(rc).Count == 0, "every key of the model decided");
        var qc = QueueModel("Reroute");
        var qcat = SimuLean.Optimization.ModelParameterCatalog.ForElement(qc.Elements.Find(x => x.Id == "q"));
        Check(qcat.Any(p => p.Key == "maxWait") && qcat.Any(p => p.Key == "onTimeout"), "the queue's maxWait / onTimeout");
        Check(SimuLean.Optimization.ModelParameterCatalog.Undecided(qc).Count == 0 && SimuLean.Optimization.ModelParameterCatalog.Undecided(PullModel()).Count == 0,
              "queue and puller keys decided");

        // errors
        c = RerouteModel(false); c.Elements.Find(x => x.Id == "S").Set("processBy.onTimeout", "Sulk");
        Check(Fails(c, "Sulk"), "an unknown action fails naming it");
        c = RerouteModel(false); c.Elements.Find(x => x.Id == "S").Remove("processBy.timeoutTarget");
        Check(Fails(c, "processBy.timeoutTarget"), "Reroute without a target fails");
        c = RerouteModel(false); c.Elements.Find(x => x.Id == "S").Set("processBy.maxWait", "3 +");
        Check(Fails(c, "processBy.maxWait"), "a bad expression fails naming the key");
        var cb = new ElementConfig("cb", "Combiner", "cb");
        c = RerouteModel(false); c.Elements.Add(cb); cb.Set("processBy.team", "ops");
        cb.Set("processBy.maxWait", "3"); cb.Set("processBy.onTimeout", "Reroute"); cb.Set("processBy.timeoutTarget", "lost");
        Check(Fails(c, "Multi-Server"), "Reroute on another station type fails (only the Multi-Server)");
        c = Cfg(20); Src(c, "src", 5); st = Srv(c, "S", 1); Snk(c, "sink"); Con(c, "src", "S"); Con(c, "S", "sink"); TeamCfg(c, "setters", 1);
        st.Set("setup.when", "EachItem"); st.Set("setup.setupBy.team", "setters");
        st.Set("setup.setupBy.maxWait", "2"); st.Set("setup.setupBy.onTimeout", "Reroute");
        Check(Fails(c, "Reroute"), "a setup crew cannot Reroute");
        c = QueueModel("Reroute"); c.Elements.Find(x => x.Id == "q").Remove("timeoutTarget");
        Check(Fails(c, "timeoutTarget"), "a queue's Reroute without a target fails");
        c = QueueModel("Reroute");
        bool notStation = false;
        try { c.Elements.Find(x => x.Id == "S").Set("maxWait", "3"); }
        catch (ArgumentException e) { notStation = e.Message.Contains("maxWait") && e.Message.Contains("processBy.maxWait"); }
        Check(notStation, "maxWait on a station (not a queue) fails, naming processBy.maxWait (when it is written)");
        c = Cfg(20); Src(c, "src", 1); var lq = Q(c, "q"); Srv(c, "S", 2); Snk(c, "sink"); Snk(c, "lost");
        c.Lists.Add(new ListConfig { Id = "L", Name = "L" });
        c.Connections.Add(new ConnectionConfig("src", "q", "General")); c.Connections.Add(new ConnectionConfig("q", "L", "PushToList"));
        var pl = new ConnectionConfig("L", "S", "PullFromList"); pl.Query = ""; c.Connections.Add(pl); Con(c, "S", "sink");
        lq.Set("maxWait", "3"); lq.Set("onTimeout", "Reroute"); lq.Set("timeoutTarget", "lost");
        Check(Fails(c, "plain connections"), "a queue pushing to a list cannot Reroute");
        lq.Set("onTimeout", "KeepWaiting");
        Check(RunCfg(c).Find("q").Timeouts > 0, "a queue pushing to a list can KeepWaiting");

        // defaults: nothing set, nothing scheduled — a model without limits has no timeout statistics
        var plain = RunCfg(QueueModel("KeepWaiting", maxWait: ""));
        Check(plain.Find("q").LimitedWaits.Count == 0 && !RunResults.Capture(plain).Find(ResultScope.Element, "q").TryGet(ResultMetrics.Timeouts, out _),
              "no max wait: no timer, no statistic");
    }
}
