// Standalone tests of preemption on everything that uses resources ("preemption everywhere"; the basic case is in
// resourcetests): the assembly station (per task), the erection dock, the stacker (cycles; crane mode), the sequencing
// station, a resource's own time at a station (HoldFor), HELD after the process (replaced), moves of several carriers,
// open-ended moves, storage moves. Without a preemption mode nothing changes (the reference baselines guard that).
// Run: dotnet run  (from StandaloneTests/preemptiontests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Naval;
using SimuLean.Resources;
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
        Run("Assembly station: a task's operator taken mid-task; Resume / Restart that task only; statistics", TestAssemblyPreempt);
        Run("Assembly station: a member's breakdown hands the task over / waits for the same member", TestAssemblyOnStop);
        Run("Erection dock: a front's crew taken mid-erection; Resume / Restart; WAITING_FOR_RESOURCE", TestDockPreempt);
        Run("Stacker: the cycle's operator taken mid-cycle; Resume / Restart", TestStackerPreempt);
        Run("Sequencing station: the carrier's operator taken mid-step; Resume / Restart that step", TestSequencingPreempt);
        Run("HoldFor: a resource's own time paused and finished by another (Resume remaining / Restart whole)", TestHoldForPreempt);
        Run("HoldFor: a member's breakdown hands its own time over / waits for it (Resume, Restart)", TestHoldForOnStop);
        Run("HELD after the process: the member taken, another replaces it; the item leaving first ends it", TestHeldReplaced);
        Run("No preemption mode: every time unchanged", TestNoModeUnchanged);
        Run("Joint move: one carrier taken while gathering; the other waits HELD at the pick-up; the replacement completes the group", TestGroupGathering);
        Run("Joint move: loaded carriers are never taken; a carrier's breakdown while loaded pauses the whole group", TestGroupLoaded);
        Run("Joint move: a replacement that never comes stays a pending request (what the stall diagnosis reads)", TestGroupNoReplacement);
        Run("Open-ended moves: under traffic an empty move ends at its next node (or where it waits); driven from outside, at its end", TestOpenEndedMoves);
        Run("Storage move and stacker crane: the carrier taken on its empty way; the move asks again", TestStorageAndCrane);
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

    /// <summary>Records what arrives and when; refuses while closed.</summary>
    sealed class Collector : Element
    {
        public readonly List<double> At = new List<double>();
        public bool Open = true;
        public Collector(SimClock c, string name = "Collector") : base(name, c) { }
        public void OpenNow() { Open = true; GetInput().NotifyAvailable(this); }
        public override bool Receive(Item i) { if (!Open) return false; At.Add(simClock.GetSimulationTime()); return true; }
        public override bool CheckAvailability(Item i) => Open;
        public override bool Unblock() => false;
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => Open ? 1 : 0;
    }

    /// <summary>Emits given items at given times (sequencing station's carriers and orders).</summary>
    sealed class Emitter : Element
    {
        readonly List<Item> held = new List<Item>();
        public Emitter(SimClock c, string name) : base(name, c) { StatsCollector.TracksContent = false; }
        public void At(double t, string type, params (string label, string value)[] labels)
        {
            var it = new Item(t);
            it.SetId(type, 0, 0);
            foreach (var l in labels) it.SetLabelValue(l.label, l.value);
            simClock.ScheduleEvent(new ActionEvent(() =>
            {
                held.Add(it);
                if (GetOutput() != null && GetOutput().SendItem(it, this)) held.Remove(it);
            }), t);
        }
        public override bool SendHeldItem(Item item)
        {
            if (!held.Contains(item) || !GetOutput().SendItem(item, this)) return false;
            held.Remove(item);
            return true;
        }
        public override bool Unblock()
        {
            bool any = false;
            foreach (var it in held.ToArray())
                if (GetOutput().SendItem(it, this)) { held.Remove(it); any = true; }
            return any;
        }
        public override int GetQueueLength() => held.Count;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
    }

    static void Link(Element from, Element to) => GeneralLink.CreateLink(from, new List<Element> { to });

    /// <summary>A Multi-Server of one slot with the team: the urgent job (or the low one) of the tests.</summary>
    static MultiServer PStation(SimClock clock, string name, double time, ResourceDispatcher d, double priority = 1,
                                PreemptMode mode = PreemptMode.None, Element sink = null, bool preemptable = true)
    {
        var s = new MultiServer(new IRandomSampler[] { C(time) }, name, clock);
        s.Resources = new StationResources(d) { Priority = _ => priority, Preempt = mode, Preemptable = preemptable };
        Link(new Up(clock, "Up" + name), s);
        Link(s, sink ?? new Sink("Sink" + name, clock));
        return s;
    }

    static double LastEntry(Element e) => e.StatsCollector.LastEntryTime;

    static (ResourceDispatcher d, TaskExecutor[] ops) Team(SimClock clock, int n, ResourceStopPolicy onStop = ResourceStopPolicy.HandOver)
    {
        var d = new ResourceDispatcher("Ops", clock);
        var ops = new TaskExecutor[n];
        for (int i = 0; i < n; i++) { ops[i] = new TaskExecutor("Op" + (i + 1), clock) { OnStop = onStop }; d.Add(ops[i]); }
        return (d, ops);
    }

    static void StartAll(ResourceDispatcher d, params Element[] elements)
    {
        foreach (var e in elements) e.Start();
        foreach (var m in d.Members) m.Start();
        d.Start();
    }

    static AssemblyStation Assembly(SimClock clock, ResourceDispatcher d, PreemptMode mode, Element sink)
    {
        var specs = new List<AssemblyTaskSpec>
        {
            new AssemblyTaskSpec { Id = "A", Duration = C(10) },
            new AssemblyTaskSpec { Id = "B", Duration = C(6) },
        };
        var a = new AssemblyStation("Asm", clock, specs, workerCount: 2) { Resources = new StationResources(d) { Preempt = mode } };
        Link(new Up(clock, "UpAsm"), a);
        Link(a, sink);
        return a;
    }

    // ── assembly station ─────────────────────────────────────────────────

    static void TestAssemblyPreempt()
    {
        foreach (var mode in new[] { PreemptMode.Resume, PreemptMode.Restart })
        {
            var clock = new SimClock(1);
            var (d, ops) = Team(clock, 2);
            var sink = new Sink("Sink", clock);
            var asm = Assembly(clock, d, PreemptMode.None, sink);
            var urgentSink = new Sink("UrgentSink", clock);
            var urgent = PStation(clock, "Urgent", 2, d, 5, mode, urgentSink);
            StartAll(d, asm, sink, urgent, urgentSink);
            asm.Receive(new Item(0));            // A (10) with Op1, B (6) with Op2
            Schedule(clock, 3, () => urgent.Receive(new Item(0)));   // takes Op1 from task A (the first of equal ones)
            Schedule(clock, 4, () => Check(asm.CurrentState == ElementState.PROCESSING && ops[1].CurrentTask != null,
                                           $"{mode}: task B goes on while A waits (the station processes)"));
            clock.AdvanceClock(40);
            Near(LastEntry(urgentSink), 5, $"{mode}: the urgent job took Op1 at 3, done at 5");
            Near(LastEntry(sink), mode == PreemptMode.Resume ? 12 : 15,
                 $"{mode}: task A {(mode == PreemptMode.Resume ? "resumed its remaining 7" : "started its 10 again")} at 5");
            Check(asm.Resources.Interruptions == 1, $"{mode}: one interruption ({asm.Resources.Interruptions})");
            Near(asm.Resources.InterruptedTime, 2, $"{mode}: task A stood paused 3 → 5");
            Check(ops[0].Preemptions == 1 && ops[1].Preemptions == 0, $"{mode}: only Op1 was taken");
        }

        // one worker: the station waits for its resource while its only task is paused
        var c2 = new SimClock(1);
        var (d2, _) = Team(c2, 1);
        var specs = new List<AssemblyTaskSpec> { new AssemblyTaskSpec { Id = "A", Duration = C(10) } };
        var sink2 = new Sink("Sink", c2);
        var asm2 = new AssemblyStation("Asm", c2, specs) { Resources = new StationResources(d2) };
        Link(new Up(c2, "UpAsm"), asm2); Link(asm2, sink2);
        var urgent2 = PStation(c2, "Urgent", 2, d2, 5, PreemptMode.Resume);
        StartAll(d2, asm2, sink2, urgent2);
        asm2.Receive(new Item(0));
        Schedule(c2, 3, () => urgent2.Receive(new Item(0)));
        Schedule(c2, 4, () => Check(asm2.CurrentState == ElementState.WAITING_FOR_RESOURCE, "one task paused: WAITING_FOR_RESOURCE"));
        c2.AdvanceClock(40);
        Near(LastEntry(sink2), 12, "resumed at 5");
        Near(asm2.GetTimeInState(ElementState.WAITING_FOR_RESOURCE), 2, "waited 2 for its operator");
    }

    static void TestAssemblyOnStop()
    {
        foreach (var policy in new[] { ResourceStopPolicy.HandOver, ResourceStopPolicy.WaitForSame })
        {
            var clock = new SimClock(1);
            var (d, ops) = Team(clock, 3, policy);
            var sink = new Sink("Sink", clock);
            var asm = Assembly(clock, d, PreemptMode.Resume, sink);
            StartAll(d, asm, sink);
            asm.Receive(new Item(0));            // A with Op1, B with Op2; Op3 free
            StopToken tok = null;
            Schedule(clock, 3, () => tok = ops[0].Stop(new StopRequest { Mode = StopMode.Immediate }));
            Schedule(clock, 7, () => ops[0].Resume(tok));
            clock.AdvanceClock(40);
            Near(LastEntry(sink), policy == ResourceStopPolicy.HandOver ? 10 : 14,
                 $"{policy}: {(policy == ResourceStopPolicy.HandOver ? "Op3 took task A over at once" : "task A waited for Op1 (3 → 7)")}");
            Check(asm.Resources.Interruptions == 1 && ops[0].Interruptions == 1, $"{policy}: one interruption by the stop");
            Check(policy == ResourceStopPolicy.HandOver ? ops[2].TasksDone == 1 : ops[2].TasksDone == 0, $"{policy}: Op3's tasks");
        }
    }

    // ── erection dock ────────────────────────────────────────────────────

    static void TestDockPreempt()
    {
        foreach (var mode in new[] { PreemptMode.Resume, PreemptMode.Restart })
        {
            var clock = new SimClock(1);
            var (d, ops) = Team(clock, 1);
            var dock = new ErectionDock("Dock", clock, C(10)) { Resources = new StationResources(d) };
            Link(new Up(clock, "UpDock"), dock);
            var urgent = PStation(clock, "Urgent", 2, d, 5, mode);
            StartAll(d, dock, urgent);
            var block = new Item(0);
            block.SetLabelValue("block", 1);
            block.SetLabelValue("erect_seq", 1);
            Check(dock.Receive(block), "the dock takes block 1");
            Schedule(clock, 3, () => urgent.Receive(new Item(0)));
            Schedule(clock, 4, () => Check(dock.Fronts[0].GetState() == ElementState.WAITING_FOR_RESOURCE
                                           && dock.CurrentState == ElementState.WAITING_FOR_RESOURCE, $"{mode}: the front waits for its crew"));
            clock.AdvanceClock(40);
            Near(dock.LastErectionTime, mode == PreemptMode.Resume ? 12 : 15, $"{mode}: erected after the pause 3 → 5");
            Check(dock.Resources.Interruptions == 1, $"{mode}: one interruption");
            Near(dock.Resources.InterruptedTime, 2, $"{mode}: interrupted 2");
        }
    }

    // ── stacker ──────────────────────────────────────────────────────────

    static void TestStackerPreempt()
    {
        foreach (var mode in new[] { PreemptMode.Resume, PreemptMode.Restart })
        {
            var clock = new SimClock(1);
            var (d, ops) = Team(clock, 1);
            var st = new Stacker("St", clock, 1, 10) { CycleTime = C(10), Resources = new StationResources(d) };
            Link(new Up(clock, "UpSt"), st);
            Link(st, new Sink("SinkSt", clock));
            var urgent = PStation(clock, "Urgent", 2, d, 5, mode);
            StartAll(d, st, urgent);
            double placed = double.NaN;
            st.ItemPlaced += (s, it, pos, level) => placed = clock.GetSimulationTime();
            st.Receive(new Item(0));
            Schedule(clock, 3, () => urgent.Receive(new Item(0)));
            Schedule(clock, 4, () => Check(st.CurrentState == ElementState.WAITING_FOR_RESOURCE, $"{mode}: the cycle waits for its operator"));
            clock.AdvanceClock(40);
            Near(placed, mode == PreemptMode.Resume ? 12 : 15, $"{mode}: placed after the pause 3 → 5");
            Check(st.Resources.Interruptions == 1 && ops[0].Preemptions == 1, $"{mode}: one interruption");
        }
    }

    // ── sequencing station ───────────────────────────────────────────────

    static void TestSequencingPreempt()
    {
        foreach (var mode in new[] { PreemptMode.Resume, PreemptMode.Restart })
        {
            var clock = new SimClock(1);
            var (d, ops) = Team(clock, 1);
            var seq = new SequencingStation("Seq", clock) { CarrierCapacity = 1, GapTime = C(10), Resources = new StationResources(d) };
            var carriers = new Emitter(clock, "Carriers"); var orders = new Emitter(clock, "Orders");
            var outc = new Collector(clock, "Out"); var orderSink = new Sink("OrderSink", clock);
            Link(carriers, seq); Link(seq, outc);
            Link(orders, seq.GetPort(SequencingStation.PortOrders));
            Link(seq.GetPort(SequencingStation.PortOrders), orderSink);
            var urgent = PStation(clock, "Urgent", 2, d, 5, mode);
            StartAll(d, seq, carriers, orders, outc, orderSink, urgent);
            carriers.At(0, "Wagon");
            orders.At(0, "Order", ("vehicle", "1"));   // no reference: a gap of 10 (the operator's carrier job)
            Schedule(clock, 3, () => urgent.Receive(new Item(0)));
            Schedule(clock, 4, () => Check(seq.CurrentState == ElementState.WAITING_FOR_RESOURCE, $"{mode}: the step waits for its operator"));
            clock.AdvanceClock(40);
            Check(outc.At.Count == 1, $"{mode}: the carrier left");
            if (outc.At.Count == 1) Near(outc.At[0], mode == PreemptMode.Resume ? 12 : 15, $"{mode}: the gap step paused 3 → 5");
            Check(seq.Resources.Interruptions == 1, $"{mode}: one interruption");
        }
    }

    // ── HoldFor: a resource's own time ───────────────────────────────────

    static MultiServer HoldForStation(SimClock clock, ResourceDispatcher d, PreemptMode mode, Sink sink)
    {
        var m = new MultiServer(new IRandomSampler[] { C(10) }, "M", clock);
        m.Resources = new StationResources(d) { HoldFor = C(6), Preempt = mode };
        Link(new Up(clock, "UpM"), m);
        Link(m, sink);
        return m;
    }

    static List<double> TaskEnds(TaskExecutor[] ops, string requester)
    {
        var ends = new List<double>();
        foreach (var op in ops)
            op.TaskFinished += (ex, t) => { if (t.Requester?.Name == requester) ends.Add(t.EndTime); };
        return ends;
    }

    static void TestHoldForPreempt()
    {
        foreach (var mode in new[] { PreemptMode.Resume, PreemptMode.Restart })
        {
            var clock = new SimClock(1);
            var (d, ops) = Team(clock, 1);
            var sink = new Sink("Sink", clock);
            var m = HoldForStation(clock, d, PreemptMode.None, sink);
            var urgent = PStation(clock, "Urgent", 2, d, 5, mode);
            StartAll(d, m, sink, urgent);
            var ends = TaskEnds(ops, "M");
            m.Receive(new Item(0));   // the operator's own time 0 → 6, the station's process 0 → 10
            Schedule(clock, 3, () => urgent.Receive(new Item(0)));
            clock.AdvanceClock(40);
            Near(LastEntry(sink), 10, $"{mode}: the station's process went on (the item stays for it)");
            Check(ends.Count == 1, $"{mode}: the own time completed once ({ends.Count})");
            if (ends.Count == 1) Near(ends[0], mode == PreemptMode.Resume ? 8 : 11, $"{mode}: own time {(mode == PreemptMode.Resume ? "remaining 3" : "whole 6")} from 5");
            Check(m.Resources.Interruptions == 1, $"{mode}: one interruption");
            Near(m.Resources.InterruptedTime, 2, $"{mode}: the own time stood still 3 → 5");
        }
    }

    static void TestHoldForOnStop()
    {
        var cases = new[]
        {
            (ResourceStopPolicy.HandOver, PreemptMode.Resume, 6.0, "Op2 takes the remaining 3 over at once"),
            (ResourceStopPolicy.WaitForSame, PreemptMode.Resume, 10.0, "Op1 back at 7 works the remaining 3"),
            (ResourceStopPolicy.WaitForSame, PreemptMode.Restart, 13.0, "Op1 back at 7 works the whole 6 again"),
        };
        foreach (var (policy, mode, end, what) in cases)
        {
            var clock = new SimClock(1);
            var (d, ops) = Team(clock, 2, policy);
            var sink = new Sink("Sink", clock);
            var m = HoldForStation(clock, d, mode, sink);
            StartAll(d, m, sink);
            var ends = TaskEnds(ops, "M");
            m.Receive(new Item(0));   // Op1
            StopToken tok = null;
            Schedule(clock, 3, () => tok = ops[0].Stop(new StopRequest { Mode = StopMode.Immediate }));
            Schedule(clock, 7, () => ops[0].Resume(tok));
            clock.AdvanceClock(40);
            Check(ends.Count == 1, $"{policy}/{mode}: the own time completed once ({ends.Count})");
            if (ends.Count == 1) Near(ends[0], end, $"{policy}/{mode}: {what}");
            Check(m.Resources.Interruptions == 1, $"{policy}/{mode}: one interruption");
            Near(LastEntry(sink), 10, $"{policy}/{mode}: the station's process unchanged");
        }
    }

    // ── HELD after the process ───────────────────────────────────────────

    static void TestHeldReplaced()
    {
        foreach (double open in new[] { 20.0, 6.0 })
        {
            var clock = new SimClock(1);
            var (d, ops) = Team(clock, 2);
            var outc = new Collector(clock, "Out") { Open = false };
            var m = new MultiServer(new IRandomSampler[] { C(2) }, "M", clock);
            m.Resources = new StationResources(d) { Release = ProcessRelease.ItemLeaves };
            Link(new Up(clock, "UpM"), m); Link(m, outc);
            var busy = PStation(clock, "Busy", 30, d, 1, PreemptMode.None, null, preemptable: false);   // keeps Op2 busy
            var urgent = PStation(clock, "Urgent", 2, d, 5, PreemptMode.Resume);
            StartAll(d, m, outc, busy, urgent);
            m.Receive(new Item(0));      // Op1: process 0 → 2, then HELD (the output is closed)
            busy.Receive(new Item(0));   // Op2
            Schedule(clock, 5, () => urgent.Receive(new Item(0)));   // takes Op1 from the HELD job
            Schedule(clock, 5.5, () => Check(ops[0].CurrentTask?.Requester == urgent && d.Requests.Count == 1,
                                             $"open {open}: Op1 on the urgent job, M asks for a replacement"));
            Schedule(clock, open, () => outc.OpenNow());
            if (open > 10)
                Schedule(clock, 10, () => Check(ops[0].CurrentTask?.Requester == m && ops[0].CurrentState == ResourceStates.HELD,
                                                "Op1 (free at 7) replaces itself: HELD for M again"));
            clock.AdvanceClock(open + 1);
            Check(outc.At.Count == 1 && Math.Abs(outc.At[0] - open) < 1e-9, $"open {open}: the item left at {open}");
            Check(m.Resources.Interruptions == 1, $"open {open}: one interruption");
            Near(m.Resources.InterruptedTime, open > 10 ? 2 : 1, $"open {open}: without its holder {(open > 10 ? "5 → 7" : "5 → 6 (the item left)")}");
            if (open < 10)
            {
                clock.AdvanceClock(8);
                Check(d.Requests.Count == 0 && ops[0].IsAvailable, "the replacement request was withdrawn: Op1 free after the urgent job");
            }
            else Check(ops[0].IsAvailable, "Op1 released when the item left");
        }
    }

    // ── no mode ──────────────────────────────────────────────────────────

    static void TestNoModeUnchanged()
    {
        var clock = new SimClock(1);
        var (d, ops) = Team(clock, 2);
        var sink = new Sink("Sink", clock);
        var asm = Assembly(clock, d, PreemptMode.None, sink);
        var urgentSink = new Sink("UrgentSink", clock);
        var urgent = PStation(clock, "Urgent", 2, d, 5, PreemptMode.None, urgentSink);
        var hfSink = new Sink("HfSink", clock);
        StartAll(d, asm, sink, urgent, urgentSink);
        asm.Receive(new Item(0));
        Schedule(clock, 3, () => urgent.Receive(new Item(0)));
        StopToken tok = null;
        Schedule(clock, 4, () => tok = ops[0].Stop(new StopRequest { Mode = StopMode.Immediate }));
        Schedule(clock, 5, () => ops[0].Resume(tok));
        clock.AdvanceClock(40);
        Near(LastEntry(sink), 10, "the assembly job keeps its operators (a stop of a member does not interrupt it)");
        Near(LastEntry(urgentSink), 8, "the urgent job waits for the first free operator (Op2 at 6)");
        Check(ops[0].Preemptions + ops[1].Preemptions == 0 && asm.Resources.Interruptions == 0 && !d.Preemptive, "nothing preempted");
    }

    // ── transport ────────────────────────────────────────────────────────

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

    static TaskExecutor Vehicle(SimClock clock, string name, string home, ITravelModel travel)
        => new TaskExecutor(name, clock, ResourceMode.Spatial) { HomeNode = home, TravelModel = travel };

    /// <summary>A one-slot station at <paramref name="location"/> whose item is carried to <paramref name="dest"/> by
    /// <paramref name="count"/> carriers of the team.</summary>
    static MultiServer Origin(SimClock clock, ResourceDispatcher d, string location, Element dest, int count,
                              Func<Item, Element, double> travel = null)
    {
        var m = new MultiServer(new IRandomSampler[] { C(1) }, "Origin", clock) { Location = location };
        Link(new Up(clock, "UpOrigin"), m);
        var link = ResourceTransportLink.Create(m, new[] { dest }, new StationResources(d, new ResourceRequirement(count)));
        if (travel != null) link.TravelTime = travel;
        return m;
    }

    static void TestGroupGathering()
    {
        // F1 (lead, from A) and F2 (from B) gather at C to carry an item to D; at 5 an urgent job at A takes F1 on its way
        var clock = new SimClock(1);
        var net = Line("A", "B", "C", "D");
        var d = new ResourceDispatcher("Forks", clock);
        var f1 = Vehicle(clock, "F1", "A", new NetworkTravel(net, 1)); var f2 = Vehicle(clock, "F2", "B", new NetworkTravel(net, 1));
        d.Add(f1); d.Add(f2);
        var sink = new Sink("Sink", clock) { Location = "D" };
        var origin = Origin(clock, d, "C", sink, 2);
        var urgentSink = new Sink("UrgentSink", clock);
        var urgent = PStation(clock, "Urgent", 2, d, 5, PreemptMode.Resume, urgentSink);
        urgent.Location = "A";
        StartAll(d, origin, sink, urgent, urgentSink);
        origin.Receive(new Item(0));   // processed 0 → 1; F1 A → C (1 → 21), F2 B → C (1 → 11)
        Schedule(clock, 5, () => urgent.Receive(new Item(0)));
        Schedule(clock, 15, () => Check(f2.CurrentState == ResourceStates.HELD && f2.Location == "C" && f1.CurrentTask?.Requester == urgent,
                                        $"F2 waits HELD at the pick-up, F1 on the urgent job ({f2.CurrentState}, {f2.Location})"));
        clock.AdvanceClock(100);
        // F1: drains to B (11), back to A (21), urgent 21 → 23; then the replacement (F1 again): A → C 23 → 43, carried C → D 53
        Near(LastEntry(urgentSink), 23, "the urgent job: F1 drained to B, back to A, processed 21 → 23");
        Near(LastEntry(sink), 53, "the group complete again at 43, delivered at 53");
        Near(f2.GetTimeInState(ResourceStates.HELD), 32, "F2 held at the pick-up 11 → 43");
        Check(f1.Preemptions == 1 && f2.Preemptions == 0, "only F1 was taken");
        Check(f1.Location == "D" && f2.Location == "D", $"both carriers end at the destination ({f1.Location}, {f2.Location})");
        Check(f1.IsAvailable && f2.IsAvailable && d.Requests.Count == 0, "both free, nothing pending");
    }

    static void TestGroupLoaded()
    {
        foreach (bool breakdown in new[] { false, true })
        {
            // two abstract carriers move an item together for 10 (1 → 11); an urgent request at 5 cannot take them; with
            // the breakdown, Op2's stop 3 → 7 pauses the group
            var clock = new SimClock(1);
            var (d, ops) = Team(clock, 2);
            var sink = new Sink("Sink", clock);
            var origin = Origin(clock, d, null, sink, 2, (item, dest) => 10);
            var urgentSink = new Sink("UrgentSink", clock);
            var urgent = PStation(clock, "Urgent", 2, d, 5, PreemptMode.Resume, urgentSink);
            StartAll(d, origin, sink, urgent, urgentSink);
            origin.Receive(new Item(0));
            Schedule(clock, 5, () => urgent.Receive(new Item(0)));
            StopToken tok = null;
            if (breakdown)
            {
                Schedule(clock, 3, () => tok = ops[1].Stop(new StopRequest { Mode = StopMode.Immediate }));
                Schedule(clock, 4, () => Check(ops[0].IsGroupHeld && ops[0].CurrentState == ResourceStates.TRAVELING_LOADED,
                                               "the lead is held with the load on (no hand-over)"));
                Schedule(clock, 7, () => ops[1].Resume(tok));
            }
            clock.AdvanceClock(100);
            string w = breakdown ? "breakdown: " : "";
            Near(LastEntry(sink), breakdown ? 15 : 11, w + (breakdown ? "the group paused 3 → 7, delivered at 15" : "delivered at 11"));
            Near(LastEntry(urgentSink), breakdown ? 17 : 13, w + "the urgent job waited for the delivery");
            Check(ops[0].Preemptions + ops[1].Preemptions == 0, w + "loaded carriers never preempted");
        }
    }

    static void TestGroupNoReplacement()
    {
        // F1 taken while gathering by a job that never ends (a hold): no replacement can come — the move's request stays
        // pending, F2 waits at the pick-up (no silent hang: the dispatcher lists the request)
        var clock = new SimClock(1);
        var net = Line("A", "B", "C");
        var d = new ResourceDispatcher("Forks", clock);
        var f1 = Vehicle(clock, "F1", "A", new NetworkTravel(net, 1)); var f2 = Vehicle(clock, "F2", "C", new NetworkTravel(net, 1));
        d.Add(f1); d.Add(f2);
        var sink = new Sink("Sink", clock) { Location = "A" };
        var origin = Origin(clock, d, "C", sink, 2);
        StartAll(d, origin, sink);
        origin.Receive(new Item(0));
        Schedule(clock, 5, () => d.Request(new ResourceRequest(null, TaskType.Other,
            (r, g) => g[0].Execute(new TaskSequence(TaskType.Other, name: "Forever").Hold())) { Priority = 5, Preempt = PreemptMode.Resume }));
        clock.AdvanceClock(100);
        var pending = d.Requests.Entries.Select(e => e.Value as ResourceRequest).Where(r => r != null && r.State == RequestState.Pending).ToList();
        Check(pending.Count == 1 && pending[0].Requester == origin, "the replacement request is pending, from the origin");
        Check(f2.CurrentState == ResourceStates.HELD && f2.Location == "C" && sink.GetStatsInputCount() == 0, "F2 waits at the pick-up; nothing delivered");
    }

    /// <summary>An open-ended travel model (a move ended from outside, as by physics): the test calls Arrive.</summary>
    sealed class Driven : ITravelModel
    {
        public double Distance(string from, string to) => from == to ? 0 : 1;
        public TravelPlan Plan(TaskExecutor e, string from, string to) => new TravelPlan { Duration = double.NaN, Route = new List<string> { from, to }, Distance = 1 };
    }

    static void TestOpenEndedMoves()
    {
        // traffic: an operator walking A → D (30) for a far job is taken at 5 by a job at A: it ends at B (10), back to A
        // (20), the urgent job 20 → 21, then the far job A → D 21 → 51 → 52
        var clock = new SimClock(1);
        var net = Line("A", "B", "C", "D");
        var traffic = new TrafficNetworkTravel(new NetworkTravel(net, 1), new TrafficControl());
        var d = new ResourceDispatcher("Ops", clock);
        var op = Vehicle(clock, "Op", "A", traffic);
        d.Add(op);
        var farSink = new Sink("FarSink", clock); var nearSink = new Sink("NearSink", clock);
        var far = PStation(clock, "Far", 1, d, 1, PreemptMode.None, farSink); far.Location = "D";
        var near = PStation(clock, "Near", 1, d, 5, PreemptMode.Resume, nearSink); near.Location = "A";
        StartAll(d, far, near, farSink, nearSink);
        far.Receive(new Item(0));
        Schedule(clock, 5, () => near.Receive(new Item(0)));
        Schedule(clock, 6, () => Check(op.CurrentLeg != null && op.CurrentLeg.To == "B", $"under traffic: ends at the next node B ({op.CurrentLeg?.To})"));
        clock.AdvanceClock(100);
        Near(LastEntry(nearSink), 21, "B at 10, back to A at 20, processed 20 → 21");
        Near(LastEntry(farSink), 52, "then the far job: A → D (21 → 51), processed 51 → 52");
        Check(op.Preemptions == 1 && far.Resources.Interruptions == 1, "one preemption of the open-ended walk");

        // traffic, waiting at a node for a busy segment: it ends there, at once
        clock = new SimClock(1);
        traffic = new TrafficNetworkTravel(new NetworkTravel(net, 1), new TrafficControl());
        d = new ResourceDispatcher("Ops", clock);
        op = Vehicle(clock, "Op", "B", traffic);
        var blocker = Vehicle(clock, "Blocker", "C", traffic);   // on C–B towards A, 0 → 10 (not in the team)
        d.Add(op);
        farSink = new Sink("FarSink", clock); nearSink = new Sink("NearSink", clock);
        far = PStation(clock, "Far", 1, d, 1, PreemptMode.None, farSink); far.Location = "D";
        near = PStation(clock, "Near", 1, d, 5, PreemptMode.Resume, nearSink); near.Location = "B";
        foreach (var e in new Element[] { far, near, farSink, nearSink, blocker }) e.Start();
        op.Start(); d.Start();
        blocker.Execute(new TaskSequence(TaskType.Other).Travel("A"));
        far.Receive(new Item(0));    // op waits at B for B–C (TRAFFIC_WAIT)
        Schedule(clock, 2, () => Check(op.CurrentState == ResourceStates.TRAFFIC_WAIT, $"waiting at B for the segment ({op.CurrentState})"));
        Schedule(clock, 3, () => near.Receive(new Item(0)));   // taken at B, at once: the near job is at B
        clock.AdvanceClock(100);
        Near(LastEntry(nearSink), 4, "taken while waiting at B: the near job at B processed 3 → 4");
        Check(far.Resources.Interruptions == 1 && op.Preemptions == 1, "the waiting walk was taken");
        Check(LastEntry(farSink) > LastEntry(nearSink), $"then the far job ({LastEntry(farSink)})");

        // driven from outside (physics): an interrupted move ends where it was going, when Arrive is called
        clock = new SimClock(1);
        d = new ResourceDispatcher("Ops", clock);
        op = Vehicle(clock, "Op", "A", new Driven());
        d.Add(op);
        farSink = new Sink("FarSink", clock); nearSink = new Sink("NearSink", clock);
        far = PStation(clock, "Far", 1, d, 1, PreemptMode.None, farSink); far.Location = "D";
        near = PStation(clock, "Near", 1, d, 5, PreemptMode.Resume, nearSink); near.Location = "A";
        StartAll(d, far, near, farSink, nearSink);
        far.Receive(new Item(0));
        Schedule(clock, 5, () => near.Receive(new Item(0)));
        Schedule(clock, 8, () => Check(op.Arrive() && op.Location == "D", "the interrupted move ends at D (Arrive)"));
        Schedule(clock, 12, () => Check(op.Arrive() && op.Location == "A", "the urgent job's walk back to A ends"));
        clock.AdvanceClock(14);
        Near(LastEntry(nearSink), 13, "the urgent job processed 12 → 13");
        Check(op.Preemptions == 1 && far.Resources.Interruptions == 1, "one preemption of the driven walk");
    }

    static void TestStorageAndCrane()
    {
        // storage: the crane (home X) goes X → L1 (30) to retrieve; an urgent job at X at 5 takes it: it ends at Y (10),
        // back to X (20), urgent 20 → 22; the move asks again: X → L1 22 → 52, load → 53, L1 → D → 63, unload → 64
        var clock = new SimClock(1);
        var net = Line("X", "Y", "D", "L1");
        var d = new ResourceDispatcher("Cranes", clock);
        var crane = new TaskExecutor("Crane", clock, ResourceMode.Spatial) { HomeNode = "X", TravelModel = new NetworkTravel(net, 1), LoadTime = 1, UnloadTime = 1 };
        d.Add(crane);
        var storage = new SimuLean.Storage.StorageSystem("Store", clock) { Location = "D", Transport = new StationResources(d) };
        storage.AddLane(new SimuLean.Storage.StorageLane("lane1", new SimuLean.Storage.Coordinate(10, 0, 0), 2,
                        SimuLean.Storage.AccessDiscipline.Lifo, SimuLean.Storage.SkuConstraint.Mixed, "L1"));
        var sink = new Sink("Sink", clock);
        Link(storage, sink);
        var urgentSink = new Sink("UrgentSink", clock);
        var urgent = PStation(clock, "Urgent", 2, d, 5, PreemptMode.Resume, urgentSink); urgent.Location = "X";
        StartAll(d, storage, sink, urgent, urgentSink);
        var item = new Item(0);
        storage.Receive(item);
        double at = double.NaN;
        Check(storage.RequestRetrieval(SimuLean.Collections.ListQuery.All.Where(e => e.Item == item), i => at = clock.GetSimulationTime()), "retrieval accepted");
        Schedule(clock, 5, () => urgent.Receive(new Item(0)));
        clock.AdvanceClock(200);
        Near(LastEntry(urgentSink), 22, "storage: the urgent job took the crane on its empty way");
        Near(at, 64, "storage: the move asked again and was done after the urgent job");
        Check(crane.Preemptions == 1, "storage: one preemption");

        // stacker crane mode: the crane F → K (20) to pick; taken at 5 by a job at F: G at 10, back to F 20, urgent → 22;
        // the cycle asks again: F → K 22 → 42, K → P0 → 52 placed
        clock = new SimClock(1);
        net = Line("F", "G", "K", "P0");
        d = new ResourceDispatcher("Gantry", clock);
        crane = new TaskExecutor("Gantry", clock, ResourceMode.Spatial) { HomeNode = "F", TravelModel = new NetworkTravel(net, 1) };
        d.Add(crane);
        var st = new Stacker("St", clock, 1, 10) { PickLocation = "K", Resources = new StationResources(d) };
        st.Positions[0].Location = "P0";
        Link(new Up(clock, "UpSt"), st); Link(st, new Sink("SinkSt", clock));
        urgentSink = new Sink("UrgentSink", clock);
        urgent = PStation(clock, "Urgent", 2, d, 5, PreemptMode.Resume, urgentSink); urgent.Location = "F";
        StartAll(d, st, urgent, urgentSink);
        double placed = double.NaN;
        st.ItemPlaced += (s, it, pos, level) => placed = clock.GetSimulationTime();
        st.Receive(new Item(0));
        Schedule(clock, 5, () => urgent.Receive(new Item(0)));
        Schedule(clock, 6, () => Check(st.CurrentState == ElementState.WAITING_FOR_RESOURCE && st.PickItem != null, "stacker: the item waits at the pick point for a crane"));
        clock.AdvanceClock(200);
        Near(LastEntry(urgentSink), 22, "stacker: the urgent job took the crane on its empty way");
        Near(placed, 52, "stacker: the cycle asked again and was placed after the urgent job");
        Check(crane.Preemptions == 1 && st.ItemsStacked == 1, "stacker: one preemption, one item stacked");
    }
}
