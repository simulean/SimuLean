// Regression tests of the fixed bugs: one test per bug.
// Each test fails on the engine before the fix and passes after it (except where noted).
// Run: dotnet run  (from StandaloneTests/bugfixtests)
// Build with -p:DefineConstants=OLDAPI to compile against an engine without the new APIs
// (warm-up and other newer APIs); those tests are then skipped.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SimuLean;
using SimuLean.Downtime;
using SimuLean.Resources;
using SimuLean.Serialization;

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
        Run("#1  per-model seed: same seed identical (also in parallel), different seeds differ", TestSeeds);
        Run("#2  operator held while BLOCKED, never released twice", TestOperatorRelease);
        Run("#3  routing skips a destination whose input strategy refuses the item", TestRoutingInputStrategy);
        Run("#4  MaxQueueInputStrategy limits the queue", TestMaxQueue);
        Run("#5  Source -> CombinerInput: no item forwarded twice", TestReentrantSource);
        Run("#6  ParameterizedRouting round_robin rotates", TestParameterizedRoundRobin);
        Run("#7  Start() resets statistics", TestStartResetsStats);
        Run("#8  AssemblyStation unblocks during an AfterCurrent stop", TestAssemblyUnblockDuringStop);
        Run("#9  DowntimeTable typed dates are not day/month swapped", TestDowntimeTableDates);
        Run("#10 mutating a StopRequest does not corrupt the counters", TestMutatedStopRequest);
        Run("#11 Resume pulls upstream until full", TestResumePullsAll);
        Run("#12 cancelling work shows a pending AfterCurrent stop", TestCancelWorkShowsStop);
        Run("#13 TimetableDowntime uses the generator Mode", TestTimetableMode);
        Run("#14 TimetableDowntime.CancelPending cancels end events", TestTimetableCancelPending);
        Run("#16 state ratios since the last reset", TestStateRatioSinceReset);
#if !OLDAPI
        Run("#17 warm-up resets statistics, keeps the model", TestWarmup);
#endif
        Run("#18 invalid sampler specs fail loudly", TestSamplerSpecErrors);
        Run("#19 Combiner batchMode", TestBatchMode);
        Run("#20 progress shift after a pause", TestProgressShift);
#if !OLDAPI
        Run("#15 MultiServer slot back to IDLE after unblock", TestSlotIdleAfterUnblock);
        Run("Combiner: every input connected the normal way (first = main)", TestCombinerConnections);
        Run("Combiner: explicit input order and too many connections", TestCombinerInputOrder);
        Run("Resources: AssemblyStation (one operator per task)", TestAssemblyOperators);
        Run("Resources: Stacker (one operator per cycle)", TestStackerOperators);
#endif
        Run("#21 merge: every upstream element is unblocked, in turn", TestMergeUnblocksEveryUpstream);
        Run("#22 a Condition reading element.x admits through CanAccept and the link (B1)", TestConditionReadsElement);
        Run("#23 schedule tables are read in the invariant culture (B3)", TestScheduleCulture);
        Run("#24 Combiner pull mode SingleLabel auto-update accepts the main item (B4)", TestPullModeAutoUpdate);
        Run("#25 GateQueue release wakes the blocked upstream (B6)", TestGateReleaseWakesUpstream);
        Run("#26 a stop on a sink resumes without an exception (B7)", TestSinkStopResume);
        Run("#27 an element without links keeps its items instead of failing (R1)", TestElementWithoutLinks);
        Run("#28 model errors fail the build: unknown type, missing connection target (R2)", TestModelErrorsFail);
        Run("#29 config parameters: one conversion rule for elements, connections, downtimes (R3)", TestConfigParameters);
        Run("#30 Combiner utilisation and service time are its own (B5, single-job station)", TestCombinerUtilisation);
        // queue pass-through statistics
        Run("#31 an item that passes a queue without waiting counts in its stay time (R-9)", TestQueuePassThroughStay);
        Run("#32 stay-time standard deviation (Welford) and the stay_std / wait_std metrics (R-10c)", TestStayStdDev);
        Run("#33 a sampler without a stream warns once that its draws are not reproducible (R-10d)", TestUnassignedStreamWarns);
        Run("#34 table files: CSV quoting, short rows, .data; the source takes a table (R-10e)", TestTableReader);
        Run("#35 storage retrievals are list queries over its stock: sku, labels, depth, FIFO (A7)", TestStorageStockQueries);
        // findings of the modelling phase
        Run("#36 the time unit applies without a start date: t = 46 h with a 1 h unit is 2000-01-02 22:00 (S5, #17)", TestUndatedTimeUnit);
        Run("#37 team units: physical speeds (m/s) and load times (s) converted with the time unit (S5, #15)", TestPhysicalUnits);
        Run("#42 a finished model with surplus from an unlimited source is not a deadlock; makespan into storage; end at any elements (#26, #27)", TestFinishedWithSurplus);
        Run("#48 a deadlock through a list: the Combiners wait for their component list, the yard for the list's pullers", TestDeadlockThroughList);
        Run("#43 a Combiner holding only kits whose main supply is exhausted is starved (finished), not a deadlock; it waits for its main input (#28)", TestCombinerStarved);
        Run("#44 transport on list transfers with an Axes crane: a yard serves sub-blocks by key to 2 bays, out of order (#11)", TestYardListAxes);
        Run("#45 a stopped station does not take a shared resource for a job that cannot start", TestStoppedStationKeepsNoResource);
        Run("#46 a Multi-Server's item count stays its content: capacity 150, per-item delays, stops, blocked output", TestMultiServerCountInvariant);
        Run("#47 a Multi-Server's cost does not grow with its capacity: an unbounded delay of 100 000 slots", TestMultiServerLargeCapacity);
        Run("#49 a Multi-Server's items include the ones waiting for a resource", TestMultiServerItemsWaitingForResource);
        Run("#50 a shift session takes as many slots as fit: twelve exact 20-minute slots in 4 h, not thirteen", TestShiftSessionSlots);
        Run("#51 time-weighted content averages cover the whole run, not only the time since the first item", TestContentAverageFromRunStart);
        Run("#52 the warm-up also resets resources, teams and downtime counters", TestWarmupResetsEverything);
        Run("#53 a Multi-Server in FIFO release does not let a newly finished item overtake its blocked ones", TestMultiServerFifoNoOvertake);
        Run("#54 a Gate Queue reports an unblock only when it sent something, and records its BLOCKED time", TestGateQueueUnblock);
        Run("#55 the end of a stop releases every blocked item the receiver has room for, not one", TestResumeReleasesAllBlocked);
        Run("#56 one feed into the stock of two stations keeps both connections; an unknown connection type fails", TestStockFeedTwoStations);
        Run("#57 a decimal comma in label values and network records is a decimal comma, not a thousands separator", TestDecimalCommaInConfigs);
        Run("#58 records that cannot be read fail the build instead of being dropped: timetable intervals, holidays, lane lengths", TestUnreadableRecordsFail);
        Run("#59 hot-path bookkeeping: slot trackers keep no log, a queue's held items are a live view", TestHotPathBookkeeping);
        Run("#60 a source restarted with Start() forgets the item it held", TestSourceRestartForgetsHeldItem);
        Run("#61 a Delay holds every item at once, each for its own time; a blocked output keeps them in finishing order", TestDelayElement);
        Run("#62 probability routing: shares by weight, drawn once per item from the source's stream, reproducible", TestProbabilityRouting);
        Run("#63 labels on entry with random draws drive a repeat loop: rand() / pick() from the element's stream", TestEntryLabelsLoop);
        Run("#64 a refused hand-over leaves the item's labels as they were; random functions need an element", TestLabelsUndoneOnRefusal);
        Run("#65 item statistics at an element: time in system, P50 / P90, a share, split by a label, in RunResults", TestItemTallies);
        Run("#66 a resource released at another element goes with the item: two devices lent for a night limit the patients", TestReleaseAtElement);
        Run("#67 the model's holidays close every shift: one working calendar for two agendas", TestModelHolidays);
        Run("#68 a source's demand factor scales the arrivals: table quantities with carried fractions, inter-arrival rate", TestDemandFactor);
        Run("#69 expressions read other elements: an overflow rule between two agendas by content('…'); get / free; unknown names fail", TestExpressionsReadElements);
        Run("#70 an agenda books each patient into the first free slot of its sessions and releases it at the appointment; reserved slots, overflow, lead time, no-shows, statistics", TestAgenda);
        Run("#71 an agenda's later template, cancellations booked again, overbooking for priority patients", TestAgendaTemplatesCancelOverbook);
        Run("#72 an agenda whose patients walk releases them their walk early: they arrive at their slot (arriveOnTime)", TestAgendaArriveOnTime);
        Run("#38 end conditions: a run stopped at the sink count is the run to that time; model empty; deadlock (S2, #7, #22)", TestRunControl);
        Run("#39 matching + transport: a yard serves bays by key with a crane — direct (any accepted), list, Combiner capacity (S1, #11, #4, #8)", TestYardKitting);
        Run("#40 work points: the resources processing at a station stand at its free work nodes, not on its item point (S4, #16)", TestWorkNodes);
        Run("#41 HeadlessModelFactory.Find: elements by display name (ids are Type_Name_instanceId), ambiguous names fail (S3, #3)", TestFindByName);
        Run("#74 an agenda keeps booking past ten years of model time; a capture does not generate slots; a cancelled extra slot is given back (review2 #4, #6, #7)", TestAgendaReview2);
        Run("#73 random expressions (exponential(), rand()) as inter-arrival and failure / repair times draw from the element's stream (review2 #3)", TestRandomExpressionSamplers);
        Run("#75 a freed place goes to the origin that has waited longest, among those the destination's input strategy admits", TestFairStart);
        Run("#77 a Combiner input with a requirement of 2 fed by a FIFO queue keeps asking it until the set is complete (benchmark C1)", TestCombinerQueueFedRequirement);
        Run("#78 utilisation of a Multi-Server / Combiner leaves out the time under a stop: the slots account the stop state (benchmark C2)", TestUtilizationUnderStop);
        Run("#79 at Idle a stop nothing will end does not hide a deadlock: the element under it is named with its stop state (benchmark C4)", TestDeadlockUnderStop);
        Run("#76 pick() evaluates each weight once; a tally's rand() leaves the element's draws alone; Overbook without reserved slots fills the session first (review2 #26-#28)", TestReview2SmallRows);

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex.GetType().Name + ": " + ex.Message); }
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>Emits items at fixed intervals; holds refused items and retries on Unblock.</summary>
    // Three feeders, each with its OWN link into one small queue (as the factory and Unity wire a merge).
    // Before the fix only the last link was the queue's input: the first two feeders, once refused, were
    // never told about free room again and kept their items for ever.
    static void TestMergeUnblocksEveryUpstream()
    {
        var clock = new SimClock(1);
        var feeders = new[] { new Feeder(clock, 10, 0, "F1"), new Feeder(clock, 10, 0, "F2"), new Feeder(clock, 10, 0, "F3") };
        var q = new ItemsQueue(2, "Q", clock);
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock);
        var sink = new Sink("Sink", clock);
        foreach (var f in feeders) GeneralLink.CreateLink(f, new List<Element> { q });
        GeneralLink.CreateLink(q, new List<Element> { m });
        GeneralLink.CreateLink(m, new List<Element> { sink });
        foreach (var e in new Element[] { feeders[0], feeders[1], feeders[2], q, m, sink }) e.Start();
        foreach (var f in feeders) f.Begin();
        clock.AdvanceClock(100);
        Check(sink.GetStatsInputCount() == 30, $"all 30 items served ({sink.GetStatsInputCount()})");
        Check(feeders.All(f => f.Held == 0), "no feeder keeps items");
        // Fair turns: while all three wait, freed room goes round robin (F1 also got the very first item,
        // which needed no notification): served in the order F1 F2 F1 F2 F3 F1 F2 F3 … — nobody starves.
        clock = new SimClock(1);
        feeders = new[] { new Feeder(clock, 10, 0, "F1"), new Feeder(clock, 10, 0, "F2"), new Feeder(clock, 10, 0, "F3") };
        q = new ItemsQueue(1, "Q", clock);
        m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock);
        var col = new Collector(clock);
        foreach (var f in feeders) GeneralLink.CreateLink(f, new List<Element> { q });
        GeneralLink.CreateLink(q, new List<Element> { m });
        GeneralLink.CreateLink(m, new List<Element> { col });
        foreach (var e in new Element[] { feeders[0], feeders[1], feeders[2], q, m, col }) e.Start();
        foreach (var f in feeders) f.Begin();
        clock.AdvanceClock(15.5);
        var served = feeders.Select(f => col.Got.Count(i => i.GetTypeName() == f.GetName())).ToArray();
        Check(string.Join(",", served) == "6,5,4", $"round-robin turns between upstream elements ({string.Join(",", served)})");
    }

    sealed class Feeder : Element, IEvent
    {
        readonly Queue<Item> held = new Queue<Item>();
        readonly double interval;
        int remaining, n;
        public Feeder(SimClock c, int count, double interval, string name = "Feeder") : base(name, c)
        { remaining = count; this.interval = interval; }
        public void Begin() => simClock.ScheduleEvent(this, 0);
        public void Execute()
        {
            var it = new Item(simClock.GetSimulationTime());
            it.SetId(GetName(), ++n, 0);
            if (held.Count > 0 || !GetOutput().SendItem(it, this)) held.Enqueue(it);
            if (--remaining > 0) simClock.ScheduleEvent(this, interval);
        }
        public override bool Unblock()
        {
            bool any = false;
            while (held.Count > 0)
            {
                Item head = held.Dequeue();               // taken out before sending
                if (GetOutput().SendItem(head, this)) { any = true; continue; }
                var rest = new List<Item> { head }; rest.AddRange(held);
                held.Clear(); foreach (var r in rest) held.Enqueue(r);
                break;
            }
            return any;
        }
        public int Held => held.Count;
        public override int GetQueueLength() => held.Count;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
    }

    /// <summary>Terminal element that can be closed (refuses items) and reopened.</summary>
    sealed class Collector : Element
    {
        public readonly List<Item> Got = new List<Item>();
        public bool Open = true;
        public Collector(SimClock c, string name = "Collector") : base(name, c) { }
        public void OpenNow() { Open = true; GetInput().NotifyAvailable(this); }
        public override bool Receive(Item i) { if (!Open) return false; Got.Add(i); return true; }
        public override bool CheckAvailability(Item i) => Open;
        public override bool Unblock() => false;
        public override int GetQueueLength() => Got.Count;
        public override int GetFreeCapacity() => Open ? int.MaxValue : 0;
    }

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

    sealed class At : IEvent
    {
        readonly Action a;
        public At(Action a) { this.a = a; }
        public void Execute() => a();
    }

    sealed class Nop : IEvent { public void Execute() { } }

    static void Schedule(SimClock clock, double t, Action a) => clock.ScheduleEvent(new At(a), t - clock.GetSimulationTime());

    static List<Element> L(params Element[] e) => new List<Element>(e);

    static IRandomSampler Const(double v) => new ConstantRandomProcess(v);

    // ── #1 ───────────────────────────────────────────────────────────────────

    static int RunSeeded(int seed)
    {
        var clock = new SimClock(seed);
        var cfg = new ElementConfig("src", "InterArrivalSource", "Src");
        cfg.Parameters["interArrivalStrategyType"] = "Exponential";
        cfg.Parameters["exponentialRate"] = 1.0;
        var src = new InterArrivalSource("Src", clock, ElementBuildHelpers.BuildRandomProcess(cfg, "interArrival", clock));
        var sink = new Collector(clock);
        GeneralLink.CreateLink(src, L(sink));
        src.Start(); sink.Start();
        clock.AdvanceClock(500);
        return sink.Got.Count;
    }

    static void TestSeeds()
    {
        int a1 = RunSeeded(101), a2 = RunSeeded(101), b = RunSeeded(102);
        Check(a1 == a2, $"same seed reproducible ({a1} vs {a2})");
        Check(a1 != b, $"different seeds differ ({a1} vs {b})");
        var par = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RunSeeded(101)))).Result;
        Check(par.All(x => x == a1), "same seed identical in parallel: " + string.Join(",", par));
    }

    // ── #2 ───────────────────────────────────────────────────────────────────

    // A FIFO team of n operators; started (like the factory does) after the elements.
    static ResourceDispatcher Team(SimClock clock, int n)
    {
        var d = new ResourceDispatcher("Ops", clock);
        for (int i = 0; i < n; i++) d.Add(new TaskExecutor("Op" + (i + 1), clock));
        return d;
    }

    static void StartTeam(ResourceDispatcher d)
    {
        foreach (var m in d.Members) m.Start();
        d.Start();
    }

    static void TestOperatorRelease()
    {
        var clock = new SimClock(1);
        var team = Team(clock, 1);
        var op = team.Members[0];
        var srcA = new ReleaseSource("SrcA", clock);
        var srcB = new ReleaseSource("SrcB", clock);
        var a = new MultiServer(new[] { Const(1) }, "A", clock) { Resources = new StationResources(team) { Release = ProcessRelease.ItemLeaves } };
        var b = new MultiServer(new[] { Const(1) }, "B", clock) { Resources = new StationResources(team) { Release = ProcessRelease.ItemLeaves } };
        var outA = new Collector(clock, "OutA") { Open = false };
        var outB = new Collector(clock, "OutB") { Open = false };
        GeneralLink.CreateLink(srcA, L(a)); GeneralLink.CreateLink(a, L(outA));
        GeneralLink.CreateLink(srcB, L(b)); GeneralLink.CreateLink(b, L(outB));
        foreach (var e in new Element[] { srcA, srcB, a, b, outA, outB }) e.Start();
        StartTeam(team);

        int maxAvailable = 0;
        void Sample() => maxAvailable = Math.Max(maxAvailable, team.Members.Count(m => m.IsAvailable));

        srcA.Enqueue(new Item(0)); srcB.Enqueue(new Item(0));
        clock.AdvanceClock(1.5); Sample();
        // A finished at t=1 but its output is closed: it keeps the only operator, B keeps waiting.
        Check(b.CurrentState == ElementState.WAITING_FOR_RESOURCE, $"B waits while A is blocked (B is {b.CurrentState.Name})");
        Check(op.CurrentState == ResourceStates.HELD, $"the operator is HELD by blocked A ({op.CurrentState.Name})");
        clock.AdvanceClock(5); Sample();
        Schedule(clock, 5, () => { outA.OpenNow(); outB.OpenNow(); });
        clock.AdvanceClock(10); Sample();
        Schedule(clock, 10, () => outB.OpenNow());
        clock.AdvanceClock(20); Sample();

        Check(maxAvailable <= 1, $"available never above the team size (max seen {maxAvailable})");
        Check(op.IsAvailable && op.TasksDone == 2, $"operator free again after two jobs (tasks {op.TasksDone})");
        Check(outA.Got.Count == 1 && outB.Got.Count == 1, $"both jobs delivered ({outA.Got.Count}, {outB.Got.Count})");
    }

    // ── #3 / #4 ──────────────────────────────────────────────────────────────

    static void TestRoutingInputStrategy()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 1, 1);
        var q0 = new ItemsQueue(10, "Q0", clock, new OriginNameInputStrategy("Nobody"));
        var q1 = new ItemsQueue(10, "Q1", clock);
        var o0 = new Collector(clock, "O0") { Open = false };
        var o1 = new Collector(clock, "O1") { Open = false };
        GeneralLink.CreateLink(feeder, L(q0, q1));
        GeneralLink.CreateLink(q0, L(o0)); GeneralLink.CreateLink(q1, L(o1));
        foreach (var e in new Element[] { feeder, q0, q1, o0, o1 }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(5);
        Check(q1.GetQueueLength() == 1 && q0.GetQueueLength() == 0 && feeder.Held == 0,
              $"item routed to Q1 (Q0 {q0.GetQueueLength()}, Q1 {q1.GetQueueLength()}, held {feeder.Held})");
    }

    static void TestMaxQueue()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 3, 1);
        var q0 = new ItemsQueue(10, "Q0", clock, new MaxQueueInputStrategy(2));
        var q1 = new ItemsQueue(10, "Q1", clock);
        var o0 = new Collector(clock, "O0") { Open = false };
        var o1 = new Collector(clock, "O1") { Open = false };
        GeneralLink.CreateLink(feeder, L(q0, q1));
        GeneralLink.CreateLink(q0, L(o0)); GeneralLink.CreateLink(q1, L(o1));
        foreach (var e in new Element[] { feeder, q0, q1, o0, o1 }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(5);
        Check(q0.GetQueueLength() == 2 && q1.GetQueueLength() == 1,
              $"MaxQueue(2): Q0 {q0.GetQueueLength()} (want 2), Q1 {q1.GetQueueLength()} (want 1)");
    }

    // ── #5 / #19 ─────────────────────────────────────────────────────────────

    /// <param name="busyOperatorUntil">If positive, the combiner uses a team of one operator that another
    /// station holds until that time: the combiner then waits for the operator with its component
    /// inputs still open (IsMainReceiving is true while WAITING_FOR_RESOURCE) — the case where a
    /// re-entrant Unblock of the source re-sent its pending item into the freed input.</param>
    static (Collector outp, int componentsIn) CombinerLine(bool batchMode, double busyOperatorUntil = 0)
    {
        var clock = new SimClock(1);
        var mainSrc = new InterArrivalSource("Main", clock, Const(10));
        var compSrc = new InterArrivalSource("Comp", clock, Const(1));
        var comb = new Combiner(new[] { 2 }, Const(0.5), "Comb", clock, batchMode: batchMode);
        var outp = new Collector(clock);
        GeneralLink.CreateLink(mainSrc, L(comb));
        GeneralLink.CreateLink(compSrc, L(comb.GetComponentInput(0)));
        GeneralLink.CreateLink(comb, L(outp));
        foreach (var e in new Element[] { mainSrc, compSrc, comb, outp }) e.Start();
        if (busyOperatorUntil > 0)
        {
            var team = Team(clock, 1);
            StartTeam(team);
            var other = new StationResources(team);
            var held = other.Acquire(mainSrc, null, () => { });
            comb.Resources = new StationResources(team);
            Schedule(clock, busyOperatorUntil, () => other.ProcessEnded(held));
        }
        clock.AdvanceClock(100);
        return (outp, (int)compSrc.GetStatsOutputCount());
    }

    static void TestReentrantSource()
    {
        var (outp, componentsIn) = CombinerLine(batchMode: true, busyOperatorUntil: 30);
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        int dup = 0, wrongCount = 0;
        foreach (var unit in outp.Got)
        {
            var subs = unit.GetSubItems() ?? new List<Item>();
            if (subs.Count != 2) wrongCount++;
            foreach (var c in subs) if (!seen.Add(c)) dup++;
        }
        Check(outp.Got.Count > 0, "combined units produced");
        Check(dup == 0, $"no component used twice ({dup} duplicates)");
        Check(wrongCount == 0, $"every unit carries exactly 2 components ({wrongCount} wrong)");
        Check(seen.Count <= componentsIn, $"components used ({seen.Count}) <= components sent ({componentsIn})");
    }

    static void TestBatchMode()
    {
        var (withBatch, _) = CombinerLine(batchMode: true);
        var (noBatch, _) = CombinerLine(batchMode: false);
        Check(withBatch.Got.Count > 0 && withBatch.Got.All(u => (u.GetSubItems()?.Count ?? 0) == 2), "batchMode=true: components travel as sub-items");
        Check(noBatch.Got.Count > 0 && noBatch.Got.All(u => (u.GetSubItems()?.Count ?? 0) == 0), "batchMode=false: components consumed");
    }

    static void TestCombinerQueueFedRequirement()
    {
        // QA, QB → Combiner (1 A + 2 B): the second B waited in QB, queued behind nothing but never offered — 2 products.
        int Products(bool bQueued)
        {
            var clock = new SimClock(1);
            var srcA = new InterArrivalSource("A", clock, Const(4));
            var srcB = new InterArrivalSource("B", clock, Const(1.5));
            var qa = new ItemsQueue(1000, "QA", clock);
            var qb = new ItemsQueue(1000, "QB", clock);
            var comb = new Combiner(new[] { 2 }, Const(1), "Asm", clock);
            var outp = new Collector(clock);
            GeneralLink.CreateLink(srcA, L(qa));
            GeneralLink.CreateLink(qa, L(comb));
            if (bQueued) { GeneralLink.CreateLink(srcB, L(qb)); GeneralLink.CreateLink(qb, L(comb.GetComponentInput(0))); }
            else GeneralLink.CreateLink(srcB, L(comb.GetComponentInput(0)));
            GeneralLink.CreateLink(comb, L(outp));
            foreach (var e in new Element[] { srcA, srcB, qa, qb, comb, outp }) e.Start();
            clock.AdvanceClock(400);
            return outp.Got.Count;
        }
        int queued = Products(true), direct = Products(false);
        // one A every 4, two B every 3: A limits, ~100 products in 400
        Check(queued >= 95, $"queue-fed component input: {queued} products (expected ~100)");
        Check(queued == direct, $"the same products as a source wired straight to the input ({queued} vs {direct})");
    }

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

    static void TestDeadlockUnderStop()
    {
        // src (every 1) → q (3) → srv (0.5) → sink; srv stopped (Immediate) at t = 2.2, resumed at `resumeAt` (0 = never).
        SimuLean.Results.RunInfo Run(double resumeAt)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "InterArrival";
            src.Parameters["interArrivalStrategyType"] = "Constant";
            src.Parameters["constantValue"] = 1.0;
            var q = new SimuLean.Serialization.ElementConfig("q", "UnityQueue", "q");
            q.Parameters["capacity"] = 3;
            var srv = new SimuLean.Serialization.ElementConfig("srv", "UnityMultiServer", "srv");
            srv.Parameters["capacity"] = 1;
            srv.Parameters["delayStrategyType"] = "Constant";
            srv.Parameters["constantValue"] = 0.5;
            c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(srv);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
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

    // ── #6 ───────────────────────────────────────────────────────────────────

    static void TestParameterizedRoundRobin()
    {
        var clock = new SimClock(1);
        clock.Context.ModelParameters["route"] = "round_robin";
        var feeder = new Feeder(clock, 4, 1);
        feeder.SetOutputStrategy(new ParameterizedRoutingStrategy("route"));
        var c0 = new Collector(clock, "C0");
        var c1 = new Collector(clock, "C1");
        GeneralLink.CreateLink(feeder, L(c0, c1));
        foreach (var e in new Element[] { feeder, c0, c1 }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(10);
        Check(c0.Got.Count == 2 && c1.Got.Count == 2, $"alternates 2/2 (got {c0.Got.Count}/{c1.Got.Count})");
    }

    // ── #7 ───────────────────────────────────────────────────────────────────

    static void TestStartResetsStats()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 5, 1);
        var q = new ItemsQueue(10, "Q", clock);
        var m = new MultiServer(new[] { Const(0.5) }, "M", clock);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(feeder, L(q)); GeneralLink.CreateLink(q, L(m)); GeneralLink.CreateLink(m, L(sink));
        foreach (var e in new Element[] { feeder, q, m, sink }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(10);
        Check(sink.GetStatsInputCount() == 5, "first run counted");
        foreach (var e in new Element[] { q, m, sink }) e.Start();
        foreach (var e in new Element[] { q, m, sink })
            Check(e.GetStatsInputCount() == 0 && e.GetStatsOutputCount() == 0 && e.GetStatsContentMax() == 0,
                  $"{e.GetName()} statistics reset by Start (in {e.GetStatsInputCount()}, out {e.GetStatsOutputCount()})");
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

    // ── #16 ──────────────────────────────────────────────────────────────────

    static void TestStateRatioSinceReset()
    {
        var t = new ElementStateTracker(ElementState.IDLE, 0);
        t.Reset(ElementState.IDLE, 100);
        t.Transition(ElementState.PROCESSING, 150);
        Near(t.GetStateRatio(ElementState.PROCESSING, 200), 0.5, "PROCESSING ratio over [100, 200]");
        Near(t.GetStateRatio(ElementState.IDLE, 200), 0.5, "IDLE ratio over [100, 200]");
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

    // ── #18 ──────────────────────────────────────────────────────────────────

    static void TestSamplerSpecErrors()
    {
        var clock = new SimClock(1);
        foreach (string bad in new[] { "Exponential~abc", "Foo~1", null, "", "Exponential", "Uniform~1" })
        {
            bool threw = false;
            try { SamplerSpec.Decode(bad, clock); }
            catch (FormatException) { threw = true; }
            catch (Exception ex) { Console.WriteLine($"  ('{bad}' threw {ex.GetType().Name})"); }
            Check(threw, $"'{bad ?? "null"}' raises FormatException");
        }
        Near(SamplerSpec.Decode("Constant~5", clock).NextValue(), 5, "valid spec still decodes");
        Check(SamplerSpec.Decode("Beta~2~5", clock) != null, "optional Beta bounds");
    }

#if !OLDAPI
    // ── #15 ──────────────────────────────────────────────────────────────────

    static void TestSlotIdleAfterUnblock()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 2, 0);
        var m = new MultiServer(new[] { Const(1), Const(5) }, "M", clock);
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(m)); GeneralLink.CreateLink(m, L(outp));
        foreach (var e in new Element[] { feeder, m, outp }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(2);                          // slot 1 done at t=1 (blocked), slot 2 busy until 5
        Schedule(clock, 2.5, () => outp.OpenNow());     // unblock while the other slot still works
        clock.AdvanceClock(3);
        var idle = (System.Collections.IEnumerable)typeof(MultiServer)
            .GetField("idleProcesses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(m);
        var states = idle.Cast<ServerProcess>().Select(sp => sp.GetState()).ToList();
        Check(states.Count == 1 && states[0] == ElementState.IDLE,
              $"freed slot is IDLE (idle pool: {string.Join(",", states)})");
    }

    // ── Combiner connections ─────────────────────────────────────────────────

    static void TestCombinerConnections()
    {
        var clock = new SimClock(1);
        var mainSrc = new InterArrivalSource("Main", clock, Const(10));
        var compA = new InterArrivalSource("CompA", clock, Const(1));
        var compB = new InterArrivalSource("CompB", clock, Const(1));
        var comb = new Combiner(new[] { 2, 1 }, Const(0.5), "Comb", clock, batchMode: true);
        var outp = new Collector(clock);
        // Same call for every input: the combiner maps them in connection order.
        GeneralLink.CreateLink(mainSrc, L(comb));
        GeneralLink.CreateLink(compA, L(comb));
        GeneralLink.CreateLink(compB, L(comb));
        GeneralLink.CreateLink(comb, L(outp));
        foreach (var e in new Element[] { mainSrc, compA, compB, comb, outp }) e.Start();
        clock.AdvanceClock(55);
        Check(outp.Got.Count >= 5, $"units produced ({outp.Got.Count})");
        Check(outp.Got.All(u => u.GetSubItems().Count == 3), "each unit: 2 from input 0 + 1 from input 1");
        Check(comb.GetComponentInput(0).GetInput() != null && comb.GetComponentInput(1).GetInput() != null, "both component ports wired");
    }

    static void TestCombinerInputOrder()
    {
        var clock = new SimClock(1);
        var mainSrc = new InterArrivalSource("Main", clock, Const(10));
        var comp = new InterArrivalSource("Comp", clock, Const(1));
        var comb = new Combiner(new[] { 1 }, Const(0.5), "Comb", clock, batchMode: true);
        var outp = new Collector(clock);
        comb.InputOrderProvider = () => new List<Element> { mainSrc, comp };
        GeneralLink.CreateLink(comp, L(comb));          // connected first, but the order says it is a component
        GeneralLink.CreateLink(mainSrc, L(comb));
        GeneralLink.CreateLink(comb, L(outp));
        foreach (var e in new Element[] { mainSrc, comp, comb, outp }) e.Start();
        clock.AdvanceClock(35);
        Check(outp.Got.Count >= 3 && outp.Got.All(u => u.GetSubItems() != null && u.GetSubItems().Count == 1),
              $"explicit order respected ({outp.Got.Count} units)");

        var extra = new InterArrivalSource("Extra", clock, Const(1));
        bool threw = false;
        try { GeneralLink.CreateLink(extra, L(comb)); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "a connection beyond main + component inputs is refused");
    }

    // ── resources on every station ──────────────────────────────────────

    static void TestAssemblyOperators()
    {
        var clock = new SimClock(1);
        var team = Team(clock, 1);
        var feeder = new Feeder(clock, 1, 1);
        var specs = new List<AssemblyTaskSpec>
        {
            new AssemblyTaskSpec { Id = "a", Duration = Const(2) },
            new AssemblyTaskSpec { Id = "b", Duration = Const(3) },
        };
        var asy = new AssemblyStation("Asy", clock, specs, workerCount: 2) { Resources = new StationResources(team) };
        var outp = new Collector(clock);
        GeneralLink.CreateLink(feeder, L(asy)); GeneralLink.CreateLink(asy, L(outp));
        foreach (var e in new Element[] { feeder, asy, outp }) e.Start();
        StartTeam(team);
        feeder.Begin();
        clock.AdvanceClock(4);
        Check(outp.Got.Count == 0, "two workers but one operator: tasks run one after the other");
        clock.AdvanceClock(6);
        Check(outp.Got.Count == 1, "job done at t = 5");
        Check(team.Members[0].IsAvailable && team.Members[0].TasksDone == 2, "operator returned after two tasks");
    }

    static void TestStackerOperators()
    {
        var clock = new SimClock(1);
        var team = Team(clock, 1);
        var feeder = new Feeder(clock, 1, 1);
        var st = new Stacker("St", clock, 1, 5) { CycleTime = Const(1), Resources = new StationResources(team) };
        var outp = new Collector(clock);
        GeneralLink.CreateLink(feeder, L(st)); GeneralLink.CreateLink(st, L(outp));
        foreach (var e in new Element[] { feeder, st, outp }) e.Start();
        StartTeam(team);
        var other = new StationResources(team);
        var held = other.Acquire(feeder, null, () => { });
        feeder.Begin();
        clock.AdvanceClock(3);
        Check(st.CurrentState == ElementState.WAITING_FOR_RESOURCE && st.ItemsStacked == 0,
              $"waits for the operator ({st.CurrentState.Name})");
        Schedule(clock, 5, () => other.ProcessEnded(held));
        clock.AdvanceClock(5.5);
        Check(st.ItemsStacked == 0, "cycle running after the operator was granted at t = 5");
        clock.AdvanceClock(7);
        Check(st.ItemsStacked == 1 && team.Members[0].IsAvailable, "placed at t = 6, operator back");
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

    // ── bugs (B1, B3, B4, B6, B7) and robustness (R1–R3) ─────────────

    static Element Feed(SimClock clock, string name, Element to, params Item[] items)
    {
        var src = new ReleaseSource(name, clock);
        GeneralLink.CreateLink(src, new List<Element> { to });
        src.Start();
        src.Enqueue(items);
        return src;
    }

    static void TestCombinerUtilisation()
    {
        var clock = new SimClock(3);
        var factory = new SimuLean.Headless.HeadlessModelFactory(clock);
        var built = factory.BuildModel(SimuLean.Testing.ReferenceModels.CombinerModel());
        factory.StartModel();
        clock.AdvanceClock(500);
        Combiner comb = null;
        foreach (var e in built.Values) if (e is Combiner c) comb = c;
        Check(comb != null && !(((Element)comb) is MultiServer), "the Combiner is a single-job station, not a MultiServer");
        double processing = comb.GetStateRatio(ElementState.PROCESSING);
        Check(processing > 0 && Math.Abs(comb.GetUtilization() - processing) < 1e-12, $"utilisation = time share processing ({comb.GetUtilization():F3}, was always 0)");
        Check(comb.GetMeanServiceTime() > 0, $"mean service time measured ({comb.GetMeanServiceTime():F3})");
    }

    static Item Labelled(string label, double value) { var i = new Item(0); i.SetLabelValue(label, value); return i; }

    static void TestConditionReadsElement()
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(10, "Q", clock, new ConditionStrategy("item.weight <= element.maxWeight"));
        q.Properties.SetDouble("maxWeight", 12);
        var sink = new Sink("K", clock);
        GeneralLink.CreateLink(q, new List<Element> { sink });
        q.Start(); sink.Start();
        Check(q.CanAccept(Labelled("weight", 5), null), "weight 5 <= 12 accepted by CanAccept");
        Check(!q.CanAccept(Labelled("weight", 20), null), "weight 20 > 12 refused");
        Feed(clock, "Src", q, Labelled("weight", 5), Labelled("weight", 7));
        Check(sink.StatsCollector.TotalInput == 2, $"both items pass through the link (got {sink.StatsCollector.TotalInput})");
    }

    static void TestScheduleCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "es-ES", "en-US" })
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
                var clock = new SimClock(1);
                var table = new Dictionary<string, List<string>>
                {
                    ["Time"] = new List<string> { "1.5", "2,5" }, ["Name"] = new List<string> { "A", "B" },
                    ["Q"] = new List<string> { "1", "2" },
                };
                var src = new ScheduleSource("S", clock, arrivalsTable: table);
                var sink = new Sink("K", clock);
                GeneralLink.CreateLink(src, new List<Element> { sink });
                src.Start(); sink.Start();
                clock.AdvanceClock(2);
                Check(sink.StatsCollector.TotalInput == 1, $"{culture}: the 1.5 row has arrived by t = 2");
                clock.AdvanceClock(3);
                Check(sink.StatsCollector.TotalInput == 3, $"{culture}: the '2,5' row (Q = 2) has arrived by t = 3");
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    static void TestPullModeAutoUpdate()
    {
        foreach (double componentType in new[] { 2.0, 1.0 })
        {
            var clock = new SimClock(1);
            var comb = new Combiner(new[] { 1 }, new SimuLean.ConstantRandomProcess(1), "C", clock,
                                    pullMode: new SingleLabelStrategy("type"));
            var sink = new Sink("K", clock);
            GeneralLink.CreateLink(comb, new List<Element> { sink });
            var main = new ReleaseSource("Main", clock); GeneralLink.CreateLink(main, new List<Element> { comb });
            var comp = new ReleaseSource("Comp", clock); GeneralLink.CreateLink(comp, new List<Element> { comb });
            foreach (var e in new Element[] { comb, sink, main, comp }) e.Start();
            main.Enqueue(Labelled("type", 2));
            Check(main.PendingCount == 0, "the main item is accepted (its value sets the pull mode)");
            comp.Enqueue(Labelled("type", componentType));
            clock.AdvanceClock(5);
            if (componentType == 2.0) Check(sink.StatsCollector.TotalInput == 1, "a matching component completes the assembly");
            else Check(sink.StatsCollector.TotalInput == 0 && comp.PendingCount == 1, "a component of another type is refused");
        }
    }

    static void TestGateReleaseWakesUpstream()
    {
        var clock = new SimClock(1);
        var src = new InterArrivalSource("S", clock, new SimuLean.ConstantRandomProcess(1));
        var gate = new GateQueue(2, "G", clock);
        var sink = new Sink("K", clock);
        GeneralLink.CreateLink(src, new List<Element> { gate });
        GeneralLink.CreateLink(gate, new List<Element> { sink });
        foreach (var e in new Element[] { src, gate, sink }) e.Start();
        clock.AdvanceClock(10.5);
        gate.Release(100);
        clock.AdvanceClock(50);
        Check(sink.StatsCollector.TotalInput >= 40, $"the source keeps sending after the release (sink got {sink.StatsCollector.TotalInput})");
        Check(src.CurrentState != ElementState.BLOCKED, "the source is not left BLOCKED");
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

    static void TestElementWithoutLinks()
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(5, "Q", clock);
        q.Start();
        Check(q.Receive(new Item(0)) && q.GetQueueLength() == 1, "a queue without an output link keeps the item");
        Check(!q.Unblock(), "Unblock without links returns false");
        var m = new MultiServer(new IRandomSampler[] { new SimuLean.ConstantRandomProcess(1) }, "M", clock);
        m.Start();
        Check(m.Receive(new Item(0)), "a server without links accepts an item");
        clock.AdvanceClock(2);
        Check(m.CurrentState == ElementState.BLOCKED, "it finishes and waits (BLOCKED) instead of failing");
    }

    static void TestModelErrorsFail()
    {
        bool Throws(SimulationConfig config, string expected)
        {
            try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(config); }
            catch (FormatException e) { return e.Message.Contains(expected); }
            return false;
        }
        var unknown = new SimulationConfig();
        unknown.Elements.Add(new ElementConfig("a", "NoSuchType", "A"));
        Check(Throws(unknown, "NoSuchType"), "an unknown element type fails the build, naming the type");

        var missing = new SimulationConfig();
        missing.Elements.Add(new ElementConfig("k", "UnitySink", "K"));
        missing.Connections.Add(new ConnectionConfig("k", "ghost"));
        Check(Throws(missing, "ghost"), "a connection to a missing element fails the build, naming it");
    }

    static void TestConfigParameters()
    {
        var conn = new ConnectionConfig("a", "b");
        conn.Parameters["port"] = 1L;                       // JSON numbers arrive as long
        Check(conn.GetParameter<int>("port", -1) == 1, "connection: long -> int");
        var dt = new DowntimeConfig("d", "MtbfMttr");
        dt.Parameters["factor"] = 3;
        Near(dt.GetParameter<double>("factor", 0), 3, "downtime: int -> double (was the default)");
        var el = new ElementConfig("e", "UnitySink", "E");
        el.Parameters["capacity"] = "abc";
        bool threw = false;
        try { el.GetParameter<int>("capacity", 0); } catch (FormatException e) { threw = e.Message.Contains("capacity"); }
        Check(threw, "a value that cannot be converted fails, naming the key");
        Check(el.GetParameter<int>("missing", 7) == 7, "a missing key gives the default");
    }

    // A queue forwards an item inside the Receive that brought it, so the link records the exit before the entry.
    // The zero wait must still be a stay-time sample, and no entry may be left behind.
    static Dictionary<string, Element> MM1(double arrivalRate, double serviceRate, bool exponential, int seed, double until)
    {
        var cfg = new SimulationConfig();
        var s = new ElementConfig("s", "UnitySource", "S");
        s.Parameters["sourceMode"] = "InterArrival";
        var q = new ElementConfig("q", "UnityQueue", "Q");
        q.Parameters["capacity"] = 1000000;
        var m = new ElementConfig("m", "UnityMultiServer", "M");
        m.Parameters["capacity"] = 1;
        if (exponential)
        {
            s.Parameters["interArrivalStrategyType"] = "Exponential"; s.Parameters["exponentialRate"] = arrivalRate;
            m.Parameters["delayStrategyType"] = "Exponential"; m.Parameters["exponentialRate"] = serviceRate;
        }
        else
        {
            s.Parameters["interArrivalStrategyType"] = "Constant"; s.Parameters["constantValue"] = 1.0 / arrivalRate;
            m.Parameters["delayStrategyType"] = "Constant"; m.Parameters["constantValue"] = 1.0 / serviceRate;
        }
        cfg.Elements.Add(s); cfg.Elements.Add(q); cfg.Elements.Add(m); cfg.Elements.Add(new ElementConfig("k", "UnitySink", "K"));
        cfg.Connections.Add(new ConnectionConfig("s", "q")); cfg.Connections.Add(new ConnectionConfig("q", "m"));
        cfg.Connections.Add(new ConnectionConfig("m", "k"));
        var clock = new SimClock(seed);
        var factory = new SimuLean.Headless.HeadlessModelFactory(clock);
        var built = factory.BuildModel(cfg);
        factory.StartModel();
        clock.AdvanceClock(until);
        return built.Values.ToDictionary(e => e.GetName());
    }

    static int TrackedEntries(Element e)
    {
        var f = typeof(SimuLean.Statistics.ElementStatsCollector).GetField("_entryTimes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return ((System.Collections.IDictionary)f.GetValue(e.StatsCollector)).Count;
    }

    static void TestQueuePassThroughStay()
    {
        // Deterministic: an arrival every 1, service 0.5 — every item passes the queue at once.
        var d = MM1(1.0, 2.0, false, 1, 100.5);
        var dq = d["Q"].StatsCollector;
        Check(dq.VarStayTime.Count == dq.TotalOutput, $"every item leaving the queue is a stay sample ({dq.VarStayTime.Count} of {dq.TotalOutput})");
        Near(dq.AvgStayTime, 0, "queue stay with no waiting");
        Check(TrackedEntries(d["Q"]) == (int)dq.ContentCurrent, $"no entry left behind ({TrackedEntries(d["Q"])} tracked, {dq.ContentCurrent} inside)");

        // M/M/1, lambda = 1, mu = 2: Wq = lambda / (mu (mu - lambda)) = 0.5 and Lq = 0.5 (Little: Wq = Lq / lambda).
        var e = MM1(1.0, 2.0, true, 3, 200000);
        var eq = e["Q"].StatsCollector;
        Check(eq.VarStayTime.Count == eq.TotalOutput, $"M/M/1: every departure is a stay sample ({eq.VarStayTime.Count} of {eq.TotalOutput})");
        Near(eq.AvgStayTime, 0.5, "M/M/1 queue stay = Wq (was 1.0 = Wq / rho)", 0.03);
        Near(eq.AvgStayTime, eq.ContentAvgTimeWeighted * eq.TotalOutput / 200000.0, "Little's law on the queue", 0.01);
        Check(eq.MinStayTime == 0, "the zero waits are in the minimum");
        Check(TrackedEntries(e["Q"]) == (int)eq.ContentCurrent, $"M/M/1: no entry left behind ({TrackedEntries(e["Q"])} tracked, {eq.ContentCurrent} inside)");
    }

    static void TestStayStdDev()
    {
        var d = MM1(1.0, 2.0, false, 1, 100.5);
        Near(d["M"].GetStatsStayTimeStdDev(), 0, "constant service: no spread");
        var e = MM1(1.0, 2.0, true, 3, 200000);
        Element m = e["M"];
        Near(m.GetStatsStayTimeMean(), 0.5, "exponential service: mean 1 / mu", 0.01);
        Near(m.GetStatsStayTimeStdDev(), 0.5, "exponential service: standard deviation = mean", 0.01);
        var t = new SimuLean.Statistics.StatTally();
        foreach (double x in new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 }) t.Update(x);
        Near(t.Average, 5, "tally mean");
        Near(t.Variance, 32.0 / 7, "tally sample variance (n - 1)");
        Check(SimuLean.Optimization.MetricCatalog.ElementStatistics.Contains("stay_std")
              && SimuLean.Optimization.MetricCatalog.ListStatistics.Contains("wait_std")
              && SimuLean.Optimization.MetricCatalog.TeamStatistics.Contains("request_wait_std"), "the catalog offers the standard deviations");
    }

    static void TestUnassignedStreamWarns()
    {
        var warnings = new List<string>();
        var previous = SimLog.WarningSink;
        SimLog.WarningSink = warnings.Add;
        try
        {
            var unseeded = new SimuLean.Stochastics.Distributions.WeibullDistribution(2, 1);   // a type no other test samples unseeded
            unseeded.Sample(); unseeded.Sample();
            new SimuLean.Stochastics.Distributions.WeibullDistribution(2, 1).Sample();
            Check(warnings.Count == 1 && warnings[0].Contains("WeibullDistribution"), $"one warning naming the type (got {warnings.Count})");

            var clock = new SimClock(5);
            var seeded = clock.Context.AssignRandomStream(new SimuLean.Stochastics.Distributions.LogNormalDistribution(0, 1), "Lathe", "delay");
            seeded.Sample();
            Check(warnings.Count == 1, "a sampler with its own stream does not warn");
        }
        finally { SimLog.WarningSink = previous; }
    }

    static void TestTableReader()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "simulean_tables");
        System.IO.Directory.CreateDirectory(dir);
        string csv = System.IO.Path.Combine(dir, "t.csv");
        System.IO.File.WriteAllText(csv, "Time,Name,Note\r\n1.5,A,\"x, y\"\r\n2,B,\"say \"\"hi\"\"\"\r\n\r\n3,C\r\n");
        var t = SimuLean.Data.TableReader.Read(csv);
        Check(t.Count == 3 && t["Time"].Count == 3, $"three columns, three rows (blank line skipped; got {t["Time"].Count})");
        Check(t["Note"][0] == "x, y", $"a quoted comma stays in the field (got '{t["Note"][0]}')");
        Check(t["Note"][1] == "say \"hi\"", $"doubled quotes (got '{t["Note"][1]}')");
        Check(t["Note"][2] == "", "a short row leaves the missing cell empty");
        string data = System.IO.Path.Combine(dir, "t.data");
        System.IO.File.WriteAllText(data, "Time  Q\n0\t2\n5 1\n");
        var d = SimuLean.Data.TableReader.Read(data);
        Check(d["Q"].Count == 2 && d["Q"][0] == "2" && d["Time"][1] == "5", ".data: whitespace-separated");
        bool threw = false;
        try { SimuLean.Data.TableReader.Read(System.IO.Path.Combine(dir, "t.xlsx")); } catch (NotSupportedException) { threw = true; }
        Check(threw, "Excel is not read by the engine");

        var clock = new SimClock(1);
        var src = new ScheduleSource("S", clock, t);
        Check(src.GetDataItemCount() == 3, "the source creates the rows of the table");
    }

    static void TestStorageStockQueries()
    {
        var clock = new SimClock(1);
        var storage = new SimuLean.Storage.StorageSystem("Store", clock);
        storage.AddLane(new SimuLean.Storage.StorageLane("deep", new SimuLean.Storage.Coordinate(0, 0, 0), 3,
            SimuLean.Storage.AccessDiscipline.Lifo, SimuLean.Storage.SkuConstraint.Mixed));
        storage.AddLane(new SimuLean.Storage.StorageLane("free", new SimuLean.Storage.Coordinate(5, 0, 0), 3,
            SimuLean.Storage.AccessDiscipline.Direct, SimuLean.Storage.SkuConstraint.Mixed));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(storage, new List<Element> { sink });
        storage.Start(); sink.Start();

        Item Make(string sku, double expiry)
        {
            var it = new Item(0);
            it.SetType(sku);
            it.SetLabelValue("expiry", expiry);
            return it;
        }
        var a1 = Make("A", 30); var b1 = Make("B", 5); var a2 = Make("A", 10);
        foreach (var it in new[] { a1, b1, a2 }) storage.Receive(it);     // all in "deep" (first lane that accepts)
        Check(storage.GetQueueLength() == 3, "three items in stock");

        var got = new List<Item>();
        var A = SimuLean.Collections.ListQuery.All.Where("sku", SimuLean.Collections.ListOp.Equal, "A");
        Check(storage.RequestRetrieval(A.OrderBy("expiry"), got.Add), "FEFO retrieval of A accepted");
        Check(got.Count == 1 && got[0] == a2, "FEFO: the A expiring first (a label, no field to define)");
        Check(storage.RequestRetrieval(A, got.Add) && got[1] == a1, "FIFO by default: the oldest A");
        Check(storage.TotalRelocations == 1, $"b1 blocked a1 at the face of the LIFO lane and was re-homed (got {storage.TotalRelocations})");
        Check(!storage.RequestRetrieval(A, got.Add), "no A left: the request fails, nothing moves");
        Check(storage.RequestRetrieval(SimuLean.Collections.ListQuery.All.Where("depth", SimuLean.Collections.ListOp.Equal, 0), got.Add)
              && got[2] == b1, "the depth field reads where the item sits now");
        Check(sink.GetStatsInputCount() == 3 && storage.GetQueueLength() == 0 && storage.StoredItems.Count == 0, "every retrieved item left through the link");
    }

    static void TestUndatedTimeUnit()
    {
        var config = new SimuLean.Serialization.SimulationConfig { TimeUnitSeconds = 3600 };
        var cal = config.CreateCalendar();
        Check(cal.SecondsPerUnit == 3600 && cal.IsDefault, $"no start date: undated calendar with the 1 h unit (unit {cal.SecondsPerUnit}, undated {cal.IsDefault})");
        Check(cal.ToDateTime(46) == new DateTime(2000, 1, 2, 22, 0, 0), $"t = 46 → {cal.ToDateTime(46):yyyy-MM-dd HH:mm}");
        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(config);
        clock.AdvanceClock(46);
        Check(clock.GetDateTime() == new DateTime(2000, 1, 2, 22, 0, 0), $"the model clock's date at t = 46: {clock.GetDateTime():yyyy-MM-dd HH:mm}");
        Check(new SimuLean.Serialization.SimulationConfig().CreateCalendar() == SimCalendar.Default, "no unit, no date: the default calendar");
        Check(SimCalendar.Undated(60).ToSimTime(new DateTime(2000, 1, 1, 2, 0, 0)) == 120, "dates convert back with the unit");
    }

    static void TestPhysicalUnits()
    {
        // 1 h time unit: an operator walking 1.2 m/s covers 4320 m per unit; a 90 s load is 0.025 units
        var clock = new SimClock(1);
        clock.Context.Calendar = SimCalendar.Undated(3600);
        var space = new SimuLean.Serialization.ModelSpace();
        space.Matrices["m"] = new SimuLean.Resources.DistanceMatrix().Set("a", "b", 2160);
        var tc = new SimuLean.Serialization.ResourceTeamConfig
        {
            Id = "ops", Travel = "Matrix", Matrix = "m", HomeNode = "a", Speed = 1.2, Units = "Physical",
            AxisSpeed = "1|0.5|1", AxisAccel = "0.2|0|0",
        };
        tc.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "op1", LoadTime = 90, UnloadTime = 180 });
        var executors = new List<SimuLean.Resources.TaskExecutor>();
        SimuLean.Serialization.ResourceBuild.BuildTeam(tc, clock, executors, space);
        var ex = executors[0];
        Check(Math.Abs(ex.LoadTime - 0.025) < 1e-12 && Math.Abs(ex.UnloadTime - 0.05) < 1e-12, $"load / unload in seconds → units ({ex.LoadTime}, {ex.UnloadTime})");
        double walk = ex.TravelModel.Plan(ex, "a", "b").Duration;
        Check(Math.Abs(walk - 0.5) < 1e-12, $"2160 m at 1.2 m/s = 1800 s = 0.5 h (got {walk})");
        Check(tc.Speed == 1.2 && tc.Members[0].LoadTime == 90, "the config itself is not changed");
        var inModel = SimuLean.Serialization.ResourceBuild.InModelUnits(tc, 3600);
        Check(inModel.AxisSpeed == "3600|1800|3600" && inModel.AxisAccel.StartsWith("2592000|"), $"axis speeds × unit, accelerations × unit² ({inModel.AxisSpeed}; {inModel.AxisAccel})");
        tc.Units = "";
        Check(ReferenceEquals(SimuLean.Serialization.ResourceBuild.InModelUnits(tc, 3600), tc), "model units: nothing converted");
        tc.Units = "Parsecs";
        bool threw = false;
        try { SimuLean.Serialization.ResourceBuild.InModelUnits(tc, 3600); } catch (ArgumentException) { threw = true; }
        Check(threw, "unknown units fail the build");
    }

    static void TestYardListAxes()
    {
        var cfg = SimuLean.Testing.ReferenceModels.YardListAxes();
        var clock = new SimClock(cfg.Seed);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(cfg);
        var order = new Dictionary<string, List<string>> { ["erectedA"] = new List<string>(), ["erectedB"] = new List<string>() };
        foreach (var sink in new[] { "erectedA", "erectedB" })
        {
            string s = sink;
            model.Find(s).ItemEntered += (el, it) => order[s].Add(Convert.ToString(it.GetLabelValue("block"), System.Globalization.CultureInfo.InvariantCulture));
        }
        model.StartModel();
        clock.AdvanceClock(cfg.MaxSimulationTime);
        var r = SimuLean.Results.RunResults.Capture(model);
        var crane = r.Find(SimuLean.Results.ResultScope.Resource, "Crane1");
        Check(string.Join(",", order["erectedA"]) == "3,1" && string.Join(",", order["erectedB"]) == "2,4",
              $"each bay gets its own sub-block, out of order (A {string.Join(",", order["erectedA"])}; B {string.Join(",", order["erectedB"])})");
        Check(crane[SimuLean.Results.ResultMetrics.Tasks] == 4, $"the crane carried the 4 sub-blocks ({crane[SimuLean.Results.ResultMetrics.Tasks]})");
        Check(r.Info.EndReason == "Empty" && !r.Info.Deadlocked, $"the run ends empty, no deadlock ({r.Info.EndReason})");
        double span = r.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan];
        Check(span > 20, $"the crane's travel on its axes takes time (makespan {span})");
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
            Check(flagged.Info.Waiting.Count == 1 && flagged.Info.Waiting[0].StartsWith("srv: BLOCKED") && flagged.Info.Waiting[0].Contains("waits for buffer"),
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

    static void TestYardKitting()
    {
        (SimuLean.Results.RunResults r, Dictionary<string, List<double>> order) Run(SimuLean.Serialization.SimulationConfig cfg)
        {
            var clock = new SimClock(cfg.Seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(cfg);
            var order = new Dictionary<string, List<double>>();
            foreach (var e in model.GetAllElements().Values)
                if (e is Sink)
                {
                    var list = order[e.GetName()] = new List<double>();
                    e.ItemEntered += (el, it) => list.Add(it.GetLabelValue("block") ?? -1);
                }
            model.StartModel();
            clock.AdvanceClock(cfg.MaxSimulationTime);
            return (SimuLean.Results.RunResults.Capture(model), order);
        }
        string Seq(List<double> l) => string.Join(",", l);

        var direct = Run(SimuLean.Testing.ReferenceModels.YardKitting());
        Check(Seq(direct.order["erectedA"]) == "3,1" && Seq(direct.order["erectedB"]) == "2,4",
              $"direct links, any accepted: each bay gets the sub-block of its own block, out of order (A {Seq(direct.order["erectedA"])}, B {Seq(direct.order["erectedB"])})");
        var model = direct.r.Find(SimuLean.Results.ResultScope.Model, "model");
        Check(model[SimuLean.Results.ResultMetrics.Makespan] == 20 && direct.r.Find(SimuLean.Results.ResultScope.Resource, "Crane1")[SimuLean.Results.ResultMetrics.Tasks] == 4,
              "the crane carries every sub-block (4 moves of 3), makespan 20 (worked by hand)");

        var fifo = Run(SimuLean.Testing.ReferenceModels.YardKitting("FIFO"));
        Check(fifo.r.Info.Deadlocked && fifo.r.Info.Waiting.Exists(w => w.StartsWith("yard:") && w.Contains("waits for bayA, bayB")),
              $"FIFO: block 1 at the head, wanted by nobody yet — the yard deadlocks ({string.Join(" | ", fifo.r.Info.Waiting)})");

        var list = Run(SimuLean.Testing.ReferenceModels.YardKittingList());
        Check(Seq(list.order["erectedA"]) == "3,1" && Seq(list.order["erectedB"]) == "2,4"
              && list.r.Find(SimuLean.Results.ResultScope.Resource, "Crane1")[SimuLean.Results.ResultMetrics.Tasks] == 4
              && list.r.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan] == 20,
              "through a list: the same matching, and the yard's crane carries each pulled sub-block (the holder's transport)");

        var cap = Run(SimuLean.Testing.ReferenceModels.YardKittingCapacity());
        Check(Seq(cap.order["erected"]) == "1,3,2,4" && cap.r.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan] == 19,
              $"one Combiner, capacity 2: each assembly matches its own main item (erected {Seq(cap.order["erected"])}, worked by hand: 1,3,2,4 by t = 19)");
        Check(cap.r.Of(SimuLean.Results.ResultScope.Slot).Count() == 2, "an assembly is a slot in the results");

        // a Multi-Server as the yard (finished items held) releases any accepted item too
        var msCfg = SimuLean.Testing.ReferenceModels.YardKitting();
        var yard = msCfg.Elements.Find(e => e.Id == "yard");
        yard.Type = "UnityMultiServer";
        yard.Parameters["capacity"] = 4;
        yard.Parameters["delayStrategyType"] = "Constant";
        yard.Parameters["constantValue"] = 0.0;
        var ms = Run(msCfg);
        Check(Seq(ms.order["erectedA"]) == "3,1" && Seq(ms.order["erectedB"]) == "2,4", "a Multi-Server releases any accepted finished item");
    }

    static void TestFindByName()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 10 };
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("UnityQueue_Buffer_1234", "UnityQueue", "Buffer"));
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("UnitySink_Out_77", "UnitySink", "Out"));
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("UnitySink_Out_78", "UnitySink", "Out"));
        var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        model.BuildModel(c);
        Check(model.Find("Buffer") != null && model.Find("Buffer").GetName() == "Buffer", "found by display name");
        Check(model.Find("UnitySink_Out_78") != null, "an id works too");
        Check(model.Find("Nope") == null && model.Find("") == null, "none: null");
        bool failed = false;
        try { model.Find("Out"); } catch (InvalidOperationException) { failed = true; }
        Check(failed, "two elements named 'Out': fails, asking for the id");
    }

    static void TestStoppedStationKeepsNoResource()
    {
        // One specialist for two stations. B's first job holds him 0..6; A's patient arrives at 1 and waits for him; A
        // stops at 3 (off shift, Immediate) until 100. B's second patient arrives at 10.
        SimuLean.Serialization.SimulationConfig Model()
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 200 };
            SimuLean.Serialization.ElementConfig Src(string id, params string[] times)
            {
                var e = new SimuLean.Serialization.ElementConfig(id, "UnitySource", id);
                e.Parameters["sourceMode"] = "Schedule";
                e.Parameters["arrivalsTable"] = new Dictionary<string, List<string>>
                {
                    ["Time"] = new List<string>(times), ["Name"] = times.Select(_ => id).ToList(), ["Q"] = times.Select(_ => "1").ToList(),
                };
                return e;
            }
            SimuLean.Serialization.ElementConfig Station(string id, double time)
            {
                var e = new SimuLean.Serialization.ElementConfig(id, "UnityMultiServer", id);
                e.Parameters["capacity"] = 1; e.Parameters["delayStrategyType"] = "Constant"; e.Parameters["constantValue"] = time;
                e.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessTeam] = "staff";
                return e;
            }
            c.Elements.Add(Src("srcA", "1")); c.Elements.Add(Src("srcB", "0", "10"));
            c.Elements.Add(Station("A", 4)); c.Elements.Add(Station("B", 6));
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("outA", "UnitySink", "outA"));
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("outB", "UnitySink", "outB"));
            foreach (var (f, t) in new[] { ("srcA", "A"), ("srcB", "B"), ("A", "outA"), ("B", "outB") })
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
            var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "staff", Name = "staff" };
            team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "Specialist" });
            c.ResourceTeams.Add(team);
            var shift = new SimuLean.Serialization.DowntimeConfig("offShift", "Timetable", "A");
            shift.Parameters["mode"] = "Immediate";
            shift.Parameters["intervals"] = new List<string>
            {
                SimuLean.Serialization.DowntimeBuilder.EncodeInterval("", new SimuLean.Downtime.DowntimeInterval(3, 97, ElementState.STOPPED)),
            };
            c.Downtimes.Add(shift);
            return c;
        }
        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(Model());
        var exits = new Dictionary<string, List<double>> { ["outA"] = new List<double>(), ["outB"] = new List<double>() };
        foreach (var sink in exits.Keys.ToList())
        {
            string s = sink;
            model.Find(s).ItemEntered += (el, it) => exits[s].Add(clock.GetSimulationTime());
        }
        model.StartModel();
        clock.AdvanceClock(200);
        Check(exits["outB"].Count == 2 && exits["outB"][1] == 16,
              $"B's second patient is served at once (10 + 6 = 16), the specialist not kept by the stopped A ({string.Join(", ", exits["outB"])})");
        Check(exits["outA"].Count == 1 && exits["outA"][0] == 104,
              $"A's patient is served when A runs again (100 + 4 = 104) ({string.Join(", ", exits["outA"])})");
    }

    static void TestMultiServerCountInvariant()
    {
        foreach (var mode in new[] { "", "Immediate", "AfterCurrent" })
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = 3000 };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "InterArrival";
            src.Parameters["interArrivalStrategyType"] = "Exponential";
            src.Parameters["exponentialRate"] = 2.0;
            var ms = new SimuLean.Serialization.ElementConfig("ms", "UnityMultiServer", "ms");
            ms.Parameters["capacity"] = 150;
            ms.Parameters["delayStrategyType"] = "Expression";
            ms.Parameters["expression"] = "item.id % 7 * 10 + 1";
            var q = new SimuLean.Serialization.ElementConfig("q", "UnityQueue", "q");   // a short buffer: the output blocks
            q.Parameters["capacity"] = 5;
            var slow = new SimuLean.Serialization.ElementConfig("slow", "UnityMultiServer", "slow");
            slow.Parameters["capacity"] = 1; slow.Parameters["delayStrategyType"] = "Constant"; slow.Parameters["constantValue"] = 0.45;
            c.Elements.Add(src); c.Elements.Add(ms); c.Elements.Add(q); c.Elements.Add(slow);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
            foreach (var (f, t) in new[] { ("src", "ms"), ("ms", "q"), ("q", "slow"), ("slow", "sink") })
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
            if (mode != "")
            {
                var d = new SimuLean.Serialization.DowntimeConfig("dt", "MtbfMttr", "ms");
                d.Parameters["ttf"] = "ExponentialMean~200"; d.Parameters["ttr"] = "ExponentialMean~30"; d.Parameters["mode"] = mode;
                c.Downtimes.Add(d);
            }
            var clock = new SimClock(7);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            var station = (MultiServer)model.Find("ms");
            model.StartModel();
            string first = null;
            bool blocked = false;
            for (double t = 10; t <= 3000 && first == null; t += 10)
            {
                clock.AdvanceClock(t);
                int held = station.GetItems().Count, count = station.GetQueueLength(), content = (int)Math.Round(station.GetStatsContentCurrent());
                if (station.CurrentState == ElementState.BLOCKED) blocked = true;
                foreach (var slot in station.Slots) if (slot.GetState() == ElementState.BLOCKED) blocked = true;
                if (count != held || count != content) first = $"t={t}: count {count}, items {held}, content {content}";
            }
            Check(first == null, $"stops '{(mode == "" ? "none" : mode)}': count = items = content all along ({first ?? "ok"})");
            Check(blocked, $"stops '{(mode == "" ? "none" : mode)}': the output did block (the case is exercised)");
        }
    }

    static void TestMultiServerLargeCapacity()
    {
        double Run(int capacity, out long delivered)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = 5000 };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "InterArrival";
            src.Parameters["interArrivalStrategyType"] = "Exponential";
            src.Parameters["exponentialRate"] = 2.0;
            var ms = new SimuLean.Serialization.ElementConfig("ms", "UnityMultiServer", "ms");
            ms.Parameters["capacity"] = capacity;
            ms.Parameters["delayStrategyType"] = "Constant";
            ms.Parameters["constantValue"] = 40.0;
            c.Elements.Add(src); c.Elements.Add(ms); c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "ms", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("ms", "sink", "General"));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var clock = new SimClock(7);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(5000);
            delivered = (long)model.Find("sink").GetStatsInputCount();
            return watch.Elapsed.TotalSeconds;
        }
        double small = Run(300, out long outSmall), big = Run(100000, out long outBig);
        Check(outSmall == outBig, $"an unbounded delay: the same items out ({outSmall}, {outBig})");
        Check(big < 8, $"100 000 slots run 5000 time units in {big:F2} s (it took ~30 s when every event walked every slot; 300 slots: {small:F2} s)");
    }

    static void TestWorkNodes()
    {
        SimuLean.Serialization.SimulationConfig Model(bool withWorkNodes)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var table = new Dictionary<string, List<string>> { ["Time"] = new List<string> { "0", "50" }, ["Name"] = new List<string> { "src", "src" }, ["Q"] = new List<string> { "1", "1" } };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "Schedule"; src.Parameters["arrivalsTable"] = table;
            var st = new SimuLean.Serialization.ElementConfig("st", "UnityMultiServer", "st");
            st.Parameters["capacity"] = 1; st.Parameters["delayStrategyType"] = "Constant"; st.Parameters["constantValue"] = 10.0;
            st.Parameters["location"] = "st";
            if (withWorkNodes) st.Parameters["workNodes"] = new List<string> { "w1", "w2" };
            st.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessTeam] = "welders";
            st.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessRequirements] = new List<string> { SimuLean.Serialization.ResourceBuild.EncodeRequirement(2, "", "weld") };
            var sink = new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink");
            c.Elements.Add(src); c.Elements.Add(st); c.Elements.Add(sink);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "st", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("st", "sink", "General"));
            c.TravelMatrices.Add(new SimuLean.Serialization.TravelMatrixConfig { Id = "m", Entries = new List<string> { "home|st|10", "home|w1|12", "home|w2|14", "st|w1|2", "st|w2|2", "w1|w2|3" } });
            var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "welders", Name = "welders", Travel = "Matrix", Matrix = "m", HomeNode = "home", Speed = 1 };
            team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "w_a" });
            team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "w_b" });
            c.ResourceTeams.Add(team);
            return c;
        }
        (List<string> at, double firstDone) Run(bool withWorkNodes)
        {
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(Model(withWorkNodes));
            double done = double.NaN;
            model.GetAllElements()["sink"].ItemEntered += (e, it) => { if (double.IsNaN(done)) done = clock.GetSimulationTime(); };
            model.StartModel();
            clock.AdvanceClock(30);
            var at = model.Executors.Select(x => x.Location).ToList();
            return (at, done);
        }
        var plain = Run(false);
        Check(plain.at.All(l => l == "st") && plain.firstDone == 20, $"no work nodes: both stand on the item point (at {string.Join(",", plain.at)}; done at {plain.firstDone} = walk 10 + 10)");
        var work = Run(true);
        Check(work.at.OrderBy(x => x).SequenceEqual(new[] { "w1", "w2" }), $"work nodes: one welder at each free work point (at {string.Join(",", work.at)})");
        Check(work.firstDone == 24, $"the process starts when both arrived: walk 14 (the farther point) + 10 = 24 (got {work.firstDone})");
        bool threw = false;
        try { var bad = Model(true); bad.Elements[1].Parameters["workNodes"] = new List<string> { "nowhere" }; new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(bad); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "a work node that is not a location fails the build");
    }

    // A station of 10 slots whose jobs each need one of 2 devices: most loaded slots wait for a device. GetItems()
    // must list every held item (it listed only the ones in process and the blocked ones).
    static void TestMultiServerItemsWaitingForResource()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 3, MaxSimulationTime = 100 };
        var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
        src.Parameters["sourceMode"] = "InterArrival";
        src.Parameters["interArrivalStrategyType"] = "Constant";
        src.Parameters["constantValue"] = 1.0;
        var ms = new SimuLean.Serialization.ElementConfig("ms", "UnityMultiServer", "ms");
        ms.Parameters["capacity"] = 10; ms.Parameters["delayStrategyType"] = "Constant"; ms.Parameters["constantValue"] = 5.0;
        ms.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessTeam] = "devices";
        c.Elements.Add(src); c.Elements.Add(ms); c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "ms", "General"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("ms", "sink", "General"));
        var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "devices", Name = "devices" };
        team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "d1" });
        team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "d2" });
        c.ResourceTeams.Add(team);
        var clock = new SimClock(3);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        var station = (MultiServer)model.Find("ms");
        model.StartModel();
        string first = null; bool waited = false;
        for (double t = 0.5; t <= 100 && first == null; t += 1)
        {
            clock.AdvanceClock(t);
            int items = station.GetItems().Count, content = (int)Math.Round(station.GetStatsContentCurrent());
            foreach (var slot in station.Slots) if (slot.GetState() == ElementState.WAITING_FOR_RESOURCE) waited = true;
            if (items != content || items != station.GetQueueLength())
                first = $"t={t}: items {items}, content {content}, count {station.GetQueueLength()}";
        }
        Check(waited, "slots did wait for a device (the case is exercised)");
        Check(first == null, $"GetItems() lists the items waiting for a resource too ({first ?? "ok"})");
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
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "Schedule";
            var times = Enumerable.Range(0, days).Select(d => (d * 24 + 6).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            src.Parameters["arrivalsTable"] = new Dictionary<string, List<string>>
            {
                ["Time"] = times, ["Name"] = times.Select(_ => "p").ToList(), ["Q"] = times.Select(_ => "14").ToList(),
            };
            var q = new SimuLean.Serialization.ElementConfig("q", "UnityQueue", "q");
            q.Parameters["capacity"] = 100000;
            var st = new SimuLean.Serialization.ElementConfig("st", "UnityMultiServer", "st");
            st.Parameters["capacity"] = 1; st.Parameters["delayStrategyType"] = "Constant"; st.Parameters["constantValue"] = slot;
            c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(st); c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
            foreach (var (f, t) in new[] { ("src", "q"), ("q", "st"), ("st", "sink") })
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
            var shift = new SimuLean.Serialization.DowntimeConfig("agenda", "Shift", "st");
            shift.Parameters["pattern"] = "Mon-Fri 08:30-12:30";
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

    // ── #51 ──────────────────────────────────────────────────────────────────

    static void TestContentAverageFromRunStart()
    {
        // one item reaches a queue at t = 900 and stays there until the end (t = 1000): average content 0.1, not 1
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 1, 1);
        var q = new ItemsQueue(10, "Q", clock);
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(q)); GeneralLink.CreateLink(q, L(outp));
        foreach (var e in new Element[] { feeder, q, outp }) e.Start();
        Schedule(clock, 900, () => feeder.Begin());
        clock.AdvanceClock(1000);
        double avg = q.GetStatsContentAvgTW();
        Check(Math.Abs(avg - 0.1) < 1e-9, $"queue content average over the run: {avg} (want 0.1)");

        // same for a list: one value pushed at t = 900
        var list = new SimuLean.Collections.SimList("L", clock);
        list.Reset();
        Schedule(clock, 1900, () => list.PushValue(1));
        clock.AdvanceClock(2000);
        double lavg = list.Content.GetTimeWeightedAverage(clock.GetSimulationTime());
        Check(Math.Abs(lavg - 0.1) < 1e-9, $"list content average over the run: {lavg} (want 0.1)");
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
            d["member " + ex.GetName() + ".tasks"] = ex.TasksDone;
            d["member " + ex.GetName() + ".usage"] = ex.UsageByRequester.Values.Sum();
        }
        int i = 0;
        foreach (var g in f.DowntimeGenerators)
        {
            d["generator " + i + ".stops"] = g.StopCount;
            d["generator " + i + ".downtime"] = g.TotalDowntime;
            d["element " + g.Target.GetName() + ".stops"] = g.Target.StopCount;
            i++;
        }
        return d;
    }

    // ── #53 ──────────────────────────────────────────────────────────────────

    static void TestMultiServerFifoNoOvertake()
    {
        // item 1 finishes at t=1 with the output closed (blocked); the output opens at 1.5 without telling anyone;
        // item 2 finishes at t=2: FIFO release sends item 1 first, then item 2 - never item 2 alone, ahead of item 1
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 2, 1);
        var m = new MultiServer(new[] { Const(1), Const(1) }, "M", clock);
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(m)); GeneralLink.CreateLink(m, L(outp));
        foreach (var e in new Element[] { feeder, m, outp }) e.Start();
        feeder.Begin();
        Schedule(clock, 1.5, () => outp.Open = true);
        clock.AdvanceClock(5);
        string order = string.Join(",", outp.Got.Select(i => i.GetId()));
        Check(outp.Got.Count == 2 && outp.Got[0].GetId() < outp.Got[1].GetId(), $"both out, oldest first (got {order})");
        Check(m.GetQueueLength() == 0, $"nothing left blocked ({m.GetQueueLength()})");
    }

    // ── #54 ──────────────────────────────────────────────────────────────────

    static void TestGateQueueUnblock()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 2, 0.1);
        var g = new GateQueue(5, "G", clock);
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(g)); GeneralLink.CreateLink(g, L(outp));
        foreach (var e in new Element[] { feeder, g, outp }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(1);
        g.Release(2);                                   // output closed: nothing leaves
        Check(!g.Unblock(), "Unblock with the output still closed: false (nothing sent)");
        Check(g.CurrentState == ElementState.BLOCKED, $"blocked while the released orders cannot leave ({g.CurrentState})");
        Schedule(clock, 3, () => outp.OpenNow());
        clock.AdvanceClock(5);
        Check(outp.Got.Count == 2 && g.GetQueueLength() == 0, $"both out once the output opened ({outp.Got.Count})");
        Near(g.GetTimeInState(ElementState.BLOCKED), 2, "BLOCKED time recorded (1 -> 3)");
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

    // ── #75 ──────────────────────────────────────────────────────────────────

    sealed class OnlyFrom : InputStrategy
    {
        readonly Element allowed;
        public OnlyFrom(Element allowed) { this.allowed = allowed; }
        public override bool Admits(Element target, Item item, Element origin) => origin == allowed;
    }

    static void TestFairStart()
    {
        // two origins always waiting, one 1-slot station behind them: every freed place went to the first origin, so
        // the second waited until the first ran out; now they take turns
        (int a, int b) Race(bool onlyB)
        {
            var clock = new SimClock(1);
            var fa = new Feeder(clock, 40, 0, "A");
            var fb = new Feeder(clock, 40, 0, "B");
            var d = new MultiServer(new[] { Const(1) }, "D", clock);
            var outp = new Collector(clock);
            GeneralLink.CreateLink(L(fa, fb), L(d)); GeneralLink.CreateLink(d, L(outp));
            if (onlyB) d.SetInputStrategy(new OnlyFrom(fb));
            foreach (var e in new Element[] { fa, fb, d, outp }) e.Start();
            fa.Begin(); fb.Begin();
            clock.AdvanceClock(20.5);
            return ((int)fa.GetStatsOutputCount(), (int)fb.GetStatsOutputCount());
        }
        var (a1, b1) = Race(false);
        Check(Math.Abs(a1 - b1) <= 1 && a1 + b1 == 21, $"the origins take turns (A {a1}, B {b1}; before: A 21, B 0)");
        var (a2, b2) = Race(true);
        Check(a2 == 0 && b2 == 21, $"the oldest origin's item refused by the input strategy: the next one is served (A {a2}, B {b2})");
    }

    // ── #76 ──────────────────────────────────────────────────────────────────

    sealed class CountingRandom : SimuLean.Stochastics.Streams.IRandomEngine
    {
        readonly System.Random r = new System.Random(3);
        public int Draws;
        public double NextDouble() { Draws++; return r.NextDouble(); }
        public int NextInt(int a, int b) { Draws++; return r.Next(a, b); }
        public void SetSeed(int seed) { }
        public double NextStandardNormal() { Draws++; return 0; }
    }

    sealed class RandomOnlyScope : SimuLean.Expressions.IExpressionScope, SimuLean.Expressions.IRandomScope
    {
        public CountingRandom Rng = new CountingRandom();
        public SimuLean.Expressions.ExprValue Get(string root, string name) => SimuLean.Expressions.ExprValue.None;
        public double Now => 0;
        public SimuLean.Stochastics.Streams.IRandomEngine Random => Rng;
    }

    static void TestReview2SmallRows()
    {
        // #26: two random weights and the choice: three draws (each weight was evaluated twice: up to five)
        var scope = new RandomOnlyScope();
        var pick = SimuLean.Expressions.Expression.Parse("pick(rand(), 1, rand(), 2)");
        for (int i = 0; i < 100; i++) pick.Evaluate(scope);
        Check(scope.Rng.Draws == 300, $"three draws per pick with two random weights ({scope.Rng.Draws} in 100)");

        // #27: the sink labels each patient with its own draw; a tally drawing too must not shift them
        double MeanLabel(bool tallyDraws)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 4, MaxSimulationTime = 500 };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "InterArrival";
            src.Parameters["interArrivalStrategyType"] = "Constant";
            src.Parameters["constantValue"] = 1.0;
            var sink = new SimuLean.Serialization.ElementConfig("done", "UnitySink", "done");
            sink.Parameters["entryLabels"] = new List<string> { "x = rand()" };
            var tallies = new List<string> { "x_seen = x" };
            if (tallyDraws) tallies.Add("noise = rand()");
            sink.Parameters["tallies"] = tallies;
            c.Elements.Add(src); c.Elements.Add(sink);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "done", "General"));
            var clock = new SimClock(4);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(500);
            return model.Find("done").StatsCollector.Tallies.Get("x_seen").Mean;
        }
        double plain = MeanLabel(false), observed = MeanLabel(true);
        Check(plain == observed, $"the labels are the same with a drawing tally ({plain} / {observed})");
    }

    // ── #56 ──────────────────────────────────────────────────────────────────

    static void TestStockFeedTwoStations()
    {
        // the reference picking model with a second picking station fed by the same stock source: each StockInput
        // connection used to replace the source's output link, so the first station never got stock
        var c = SimuLean.Testing.ReferenceModels.PickingModel();
        var copy = c.Clone();
        var orders2 = copy.Elements.First(e => e.Id == "orders");
        orders2.Id = orders2.Name = "orders2";
        var pick2 = copy.Elements.First(e => e.Id == "pick");
        pick2.Id = pick2.Name = "pick2";
        c.Elements.Add(orders2); c.Elements.Add(pick2);
        c.Connections.Add(new ConnectionConfig("orders2", "pick2", "General"));
        c.Connections.Add(new ConnectionConfig("pick2", "out", "General"));
        c.Connections.Add(new ConnectionConfig("stock", "pick2", "StockInput"));
        c.Elements.First(e => e.Id == "stock").Parameters["outputStrategyType"] = "RoundRobin";   // feeds both in turn
        var clock = new SimClock(1);
        var factory = new SimuLean.Headless.HeadlessModelFactory(clock, enableLogging: false);
        var els = factory.BuildModel(c);
        factory.StartModel();
        clock.AdvanceClock(c.MaxSimulationTime);
        Check(els["pick"].GetStatsOutputCount() > 0 && els["pick2"].GetStatsOutputCount() > 0,
              $"both stations picked (pick {els["pick"].GetStatsOutputCount()}, pick2 {els["pick2"].GetStatsOutputCount()})");

        var bad = SimuLean.Testing.ReferenceModels.PickingModel();
        bad.Connections.Add(new ConnectionConfig("stock", "out", "CombinerMainInput"));
        bool threw = false;
        try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(bad); }
        catch (FormatException ex) { threw = ex.Message.Contains("CombinerMainInput"); }
        Check(threw, "an unknown connection type fails the build, naming it");
    }

    // ── #57 ──────────────────────────────────────────────────────────────────

    static void TestDecimalCommaInConfigs()
    {
        var item = new Item(0); item.SetLabelValue("w", 2.5);
        var single = new ElementConfig("q", "UnityQueue", "q");
        single.Parameters["inputStrategyType"] = "SingleLabel";
        single.Parameters["inputStrategy_singleLabelName"] = "w";
        single.Parameters["inputStrategy_singleLabelValue"] = "2,5";
        Check(ElementBuildHelpers.BuildInputStrategy(single).Admits(null, item, null), "single label value '2,5' = 2.5 (was 25: nothing admitted)");

        var multi = new ElementConfig("q", "UnityQueue", "q");
        multi.Parameters["inputStrategyType"] = "MultiLabel";
        multi.Parameters["inputStrategy_multiLabelPairs"] = new List<string> { "w=2,5" };
        Check(ElementBuildHelpers.BuildInputStrategy(multi).Admits(null, item, null), "multi label pair 'w=2,5' = 2.5");

        Check(Math.Abs(TransportStorageConfigCodec.ParseDouble("2,5") - 2.5) < 1e-12, $"network record '2,5' = 2.5 ({TransportStorageConfigCodec.ParseDouble("2,5")})");

        single.Parameters["inputStrategy_singleLabelValue"] = "abc";
        bool threw = false;
        try { ElementBuildHelpers.BuildInputStrategy(single); } catch (FormatException ex) { threw = ex.Message.Contains("abc"); }
        Check(threw, "a label value that is not a number fails, naming it (it silently admitted nothing)");
    }

    // ── #58 ──────────────────────────────────────────────────────────────────

    static void TestUnreadableRecordsFail()
    {
        var clock = new SimClock(1);
        var els = new Dictionary<string, Element> { ["m"] = new MultiServer(new[] { Const(1) }, "M", clock) };
        bool Fails(DowntimeConfig dc, string mention)
        {
            try { DowntimeBuilder.Build(dc, els, clock); return false; }
            catch (FormatException ex) { return ex.Message.Contains(mention); }
        }
        var ok = new DowntimeConfig("t", "Timetable", "m");
        ok.Parameters["intervals"] = new List<string> { "m|100|50", "m|3600,5|60" };   // a decimal comma is read as one
        Check(DowntimeBuilder.Build(ok, els, clock).Count == 1, "valid intervals build (decimal comma accepted)");
        var bad = new DowntimeConfig("t", "Timetable", "m");
        bad.Parameters["intervals"] = new List<string> { "m|100|50", "m|tomorrow|60" };
        Check(Fails(bad, "tomorrow"), "an interval that cannot be read fails, naming it (the stop silently never happened)");
        var shift = new DowntimeConfig("s", "Shift", "m");
        shift.Parameters["pattern"] = "Mon-Fri 08:00-16:00";
        shift.Parameters["holidays"] = new List<string> { "2026-12-25", "Christmas" };
        Check(Fails(shift, "Christmas"), "a holiday that is not a date fails, naming it");

        var lanes = new ElementConfig("l", "UnityLengthLimitedQueue", "l");
        lanes.Parameters["laneLengths"] = new List<string> { "12", "2,5" };
        var read = LengthLimitedQueueBuilder.ReadLaneLengths(lanes);
        Check(read.Count == 2 && Math.Abs(read[1] - 2.5) < 1e-12, $"lane lengths '12', '2,5' = two lanes, 12 and 2.5 ({string.Join(" ", read)})");
        lanes.Parameters["laneLengths"] = new List<object> { 8.0, 4L };   // a list loaded from JSON
        read = LengthLimitedQueueBuilder.ReadLaneLengths(lanes);
        Check(read.Count == 2 && read[0] == 8 && read[1] == 4, $"a JSON list of numbers ({string.Join(" ", read)}; it fell back to one lane of 10)");
        lanes.Parameters["laneLengths"] = new List<string> { "12", "x" };
        bool threw = false;
        try { LengthLimitedQueueBuilder.ReadLaneLengths(lanes); } catch (FormatException ex) { threw = ex.Message.Contains("x"); }
        Check(threw, "a lane length that is not a positive number fails");
    }

    // ── #59 ──────────────────────────────────────────────────────────────────

    static void TestHotPathBookkeeping()
    {
        var t = new ElementStateTracker(ElementState.IDLE, 0, keepLog: false);
        for (int i = 1; i <= 1000; i++) t.Transition(i % 2 == 1 ? ElementState.PROCESSING : ElementState.IDLE, i);
        Check(t.Log.Count == 1 && Math.Abs(t.GetTimeInState(ElementState.PROCESSING, 1000) - 500) < 1e-9, $"totals kept, no growing log ({t.Log.Count} entries)");

        // "any accepted" release out of a 1 000-item queue; the held items stay a live view
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 1000, 0);
        var q = new ItemsQueue(2000, "Q", clock) { Release = ReleaseMode.AnyAccepted };
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(q)); GeneralLink.CreateLink(q, L(outp));
        foreach (var e in new Element[] { feeder, q, outp }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(1);
        var view = q.GetItems();
        Check(view.Count == 1000, $"1 000 held ({view.Count})");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) outp.OpenNow();   // one release per notification of room
        clock.AdvanceClock(2);
        Check(watch.Elapsed.TotalSeconds < 1, $"1 000 releases in linear time ({watch.Elapsed.TotalSeconds:F2} s)");
        Check(outp.Got.Count == 1000 && view.Count == 0 && q.GetQueueLength() == 0, $"all released; the view the caller holds is current ({view.Count})");
    }

    // ── #60 ──────────────────────────────────────────────────────────────────

    // A Delay has no capacity — ten items arriving together with delays 10, 9, …, 1 all start at once and
    // leave in delay order; with the output blocked they wait and leave in the order they finished.
    static void TestDelayElement()
    {
        SimuLean.Serialization.SimulationConfig Model(bool gated)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "Schedule";
            src.Parameters["arrivalsTable"] = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "p" }, ["Q"] = new List<string> { "10" },
            };
            var d = new SimuLean.Serialization.ElementConfig("d", "UnityDelay", "d");
            d.Parameters["delayStrategyType"] = "Expression";
            d.Parameters["expression"] = "11 - id";
            c.Elements.Add(src); c.Elements.Add(d);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "d", "General"));
            if (gated)
            {
                var q = new SimuLean.Serialization.ElementConfig("q", "UnityQueue", "q");
                q.Parameters["capacity"] = 1;
                c.Elements.Add(q);
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("d", "q", "General"));
            }
            else
            {
                c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("d", "sink", "General"));
            }
            return c;
        }

        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(Model(false));
        var delay = (Delay)model.Find("d");
        model.StartModel();
        clock.AdvanceClock(0.5);
        Check(delay.RunningCount == 10, $"all ten in their delay at once (running {delay.RunningCount})");
        Check(delay.CurrentState == ElementState.PROCESSING, "processing while items wait");
        clock.AdvanceClock(5.5);
        Check(delay.GetQueueLength() == 5, $"five left by t=5.5 (held {delay.GetQueueLength()})");
        clock.AdvanceClock(20);
        Check(delay.GetQueueLength() == 0 && delay.CurrentState == ElementState.IDLE, "all gone and idle at the end");
        Check(Math.Abs(delay.GetStatsOutputCount() - 10) < 1e-9, $"output 10 (got {delay.GetStatsOutputCount()})");

        clock = new SimClock(1);
        model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(Model(true));
        delay = (Delay)model.Find("d");
        model.StartModel();
        clock.AdvanceClock(20);
        Check(delay.RunningCount == 0 && delay.GetQueueLength() == 9, $"one item passed, nine wait to leave (held {delay.GetQueueLength()})");
        Check(delay.CurrentState == ElementState.BLOCKED, "blocked when only finished items are held");
        Check(Math.Abs(delay.GetStatsContentCurrent() - 9) < 1e-9, "content from the links: nine");
    }

    // 6|3|1 weights over three sinks share 20 000 items about 60 / 30 / 10 %, the same counts for the same
    // seed and other counts for another.
    static void TestProbabilityRouting()
    {
        int[] Shares(int seed)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = seed, MaxSimulationTime = 20000 };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "InterArrival";
            src.Parameters["interArrivalStrategyType"] = "Constant";
            src.Parameters["constantValue"] = 1.0;
            src.Parameters["outputStrategyType"] = "Probability";
            src.Parameters["outputStrategy_weights"] = "6|3|1";
            c.Elements.Add(src);
            foreach (var n in new[] { "a", "b", "c" })
            {
                c.Elements.Add(new SimuLean.Serialization.ElementConfig(n, "UnitySink", n));
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", n, "General"));
            }
            var clock = new SimClock(seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(20000.5);
            return new[] { "a", "b", "c" }.Select(n => (int)model.Find(n).GetStatsInputCount()).ToArray();
        }
        var s1 = Shares(7);
        int total = s1.Sum();
        Check(total >= 19999, $"every item routed ({total})");
        Check(Math.Abs(s1[0] / (double)total - 0.6) < 0.015 && Math.Abs(s1[1] / (double)total - 0.3) < 0.015 && Math.Abs(s1[2] / (double)total - 0.1) < 0.01,
              $"shares 60/30/10 % ({string.Join(", ", s1)})");
        Check(Shares(7).SequenceEqual(s1), "the same counts for the same seed");
        Check(!Shares(8).SequenceEqual(s1), "other counts for another seed");
    }

    // A test that is invalid half of the time is repeated (entry labels: reps = reps + 1,
    // valid = rand() < 0.5; routing item.valid ? 0 : 1 back to its queue): every patient leaves with a valid test and
    // about two tests on average; the case mix drawn by pick() on the source's exit.
    static void TestEntryLabelsLoop()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 3, MaxSimulationTime = 5000 };
        var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
        src.Parameters["sourceMode"] = "InterArrival";
        src.Parameters["interArrivalStrategyType"] = "Constant";
        src.Parameters["constantValue"] = 3.0;
        src.Parameters["exitLabels"] = new List<string> { "dx = pick(0.75, 'diagnosis', 0.25, 'control')" };
        var q = new SimuLean.Serialization.ElementConfig("q", "UnityQueue", "q");
        q.Parameters["capacity"] = 100000;
        var st = new SimuLean.Serialization.ElementConfig("test", "UnityMultiServer", "test");
        st.Parameters["capacity"] = 1; st.Parameters["delayStrategyType"] = "Constant"; st.Parameters["constantValue"] = 1.0;
        st.Parameters["entryLabels"] = new List<string> { "reps = reps + 1", "valid = rand() < 0.5", "t_test = now()" };
        st.Parameters["outputStrategyType"] = "Expression";
        st.Parameters["outputStrategy_expression"] = "item.valid ? 0 : 1";
        var sink = new SimuLean.Serialization.ElementConfig("done", "UnitySink", "done");
        sink.Parameters["keepItems"] = true;
        c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(st); c.Elements.Add(sink);
        foreach (var (f, t) in new[] { ("src", "q"), ("q", "test"), ("test", "done"), ("test", "q") })
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
        var clock = new SimClock(3);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(5000);
        var kept = ((Sink)model.Find("done")).Kept;
        Check(kept.Count > 1000, $"patients done ({kept.Count})");
        Check(kept.All(it => it.GetLabelOrDefault("valid") == 1 && it.GetLabelOrDefault("reps") >= 1), "every patient leaves with a valid test");
        double meanReps = kept.Average(it => it.GetLabelOrDefault("reps"));
        Check(Math.Abs(meanReps - 2.0) < 0.15, $"about two tests each (geometric, p = 0.5): {meanReps:0.000}");
        double diag = kept.Count(it => it.GetLabelText("dx") == "diagnosis") / (double)kept.Count;
        Check(Math.Abs(diag - 0.75) < 0.04, $"case mix 75 % diagnosis ({diag:0.000})");
        Check(kept.All(it => it.GetLabelOrDefault("t_test") <= clock.GetSimulationTime()), "timestamps from now()");
    }

    // A full queue refuses the second item; its entry label (tries = tries + 1) is undone. Random
    // functions fail outside an element (no stream).
    static void TestLabelsUndoneOnRefusal()
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(1, "q", clock, null, new SimuLean.Headless.HeadlessVElement(false));
        q.EntryLabels = LabelAssignments.Parse(new[] { "tries = tries + 1" }, "q");
        var first = new Item(0); var second = new Item(0);
        Check(ItemHandOver.Receive(null, q, first) && first.GetLabelOrDefault("tries") == 1, "taken: labelled");
        second.SetLabelValue("tries", 5.0);
        Check(!ItemHandOver.Receive(null, q, second), "the full queue refuses the second");
        Check(second.GetLabelOrDefault("tries") == 5, $"its label as it was (5, got {second.GetLabelOrDefault("tries")})");
        var fresh = new Item(0);
        ItemHandOver.Receive(null, q, fresh);
        Check(!fresh.HasLabel("tries"), "a label it did not have is removed again");
        bool threw = false;
        try { SimuLean.Expressions.Expression.Parse("rand()").Evaluate(new SimuLean.Expressions.EntityScope()); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "rand() without an element fails");
        bool bad = false;
        try { LabelAssignments.Parse(new[] { "x == 1" }, "q"); } catch (FormatException) { bad = true; }
        Check(bad, "a record that is not 'name = expression' fails");
    }

    // Patients take Uniform(1, 3) at a station; the sink records time_in_system = age and
    // fast = age <= 2 split by the case mix label. Mean ~2, P50 ~2, P90 ~2.8, the share ~0.5; the splits add up to the
    // total; RunResults carries them as tally:… statistics.
    static void TestItemTallies()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 5, MaxSimulationTime = 20000 };
        var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
        src.Parameters["sourceMode"] = "InterArrival";
        src.Parameters["interArrivalStrategyType"] = "Constant";
        src.Parameters["constantValue"] = 4.0;
        src.Parameters["exitLabels"] = new List<string> { "dx = pick(1, 'diagnosis', 1, 'control')" };
        var st = new SimuLean.Serialization.ElementConfig("st", "UnityMultiServer", "st");
        st.Parameters["capacity"] = 1; st.Parameters["delayStrategyType"] = "Uniform";
        st.Parameters["uniformMin"] = 1.0; st.Parameters["uniformMax"] = 3.0;
        var sink = new SimuLean.Serialization.ElementConfig("done", "UnitySink", "done");
        sink.Parameters["tallies"] = new List<string> { "time_in_system = age", "fast = age <= 2" };
        sink.Parameters["tallySplit"] = "dx";
        c.Elements.Add(src); c.Elements.Add(st); c.Elements.Add(sink);
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "st", "General"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("st", "done", "General"));
        var clock = new SimClock(5);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(20000);
        var done = model.Find("done");
        var tallies = done.StatsCollector.Tallies;
        var t = tallies.Get("time_in_system");
        Check(t.Count == (int)done.GetStatsInputCount(), $"one value per patient ({t.Count} / {done.GetStatsInputCount()})");
        Check(Math.Abs(t.Mean - 2) < 0.05 && Math.Abs(t.Percentile(0.5) - 2) < 0.08 && Math.Abs(t.Percentile(0.9) - 2.8) < 0.08,
              $"mean {t.Mean:0.000}, P50 {t.Percentile(0.5):0.000}, P90 {t.Percentile(0.9):0.000}");
        Check(t.Min >= 1 && t.Max <= 3, "within the process time range");
        Check(Math.Abs(tallies.Get("fast").Mean - 0.5) < 0.03, $"share within 2: {tallies.Get("fast").Mean:0.000}");
        var d = tallies.Get("time_in_system", "diagnosis"); var k = tallies.Get("time_in_system", "control");
        Check(d != null && k != null && d.Count + k.Count == t.Count, "the splits add up to the total");
        var r = SimuLean.Results.RunResults.Capture(model, "tallies");
        var o = r.Objects.First(x => x.Name == "done");
        Check(o.TryGet("tally:time_in_system:p90", out double p90) && Math.Abs(p90 - t.Percentile(0.9)) < 1e-12, "tally:time_in_system:p90 in the results");
        Check(o.TryGet("tally:fast[control]:count", out double kc) && kc == k.Count, "split statistics in the results");
        var study = new SimuLean.Optimization.Study(c).Metric("p90", "done.tally:time_in_system:p90")
            .Metric("n", "done.tally:fast[control]:count").WithRunTime(20000).Run();
        Check(Math.Abs(study.Runs[0].Metrics["p90"] - 2.8) < 0.08 && Math.Abs(study.Runs[0].Metrics["n"] - k.Count) < 100,
              $"a study asks for a tally statistic (Owner.tally:NAME:stat; its own seed): P90 {study.Runs[0].Metrics["p90"]:0.000}, {study.Runs[0].Metrics["n"]} controls");
        Check(!r.Objects.First(x => x.Name == "st").TryGet("utilization_onshift", out _), "no on-shift utilisation without a shift");
    }

    // Two polygraphs (a team without travel) are handed out at the delivery desk and come back when the
    // patient enters the return desk after a night at home (a Delay of 10). Patients arrive every 1: about two
    // patients per 10.1 time units get through; released at the end of the delivery, all of them would.
    static void TestReleaseAtElement()
    {
        (int done, double maxInUse) Run(string release)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 200 };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "InterArrival";
            src.Parameters["interArrivalStrategyType"] = "Constant";
            src.Parameters["constantValue"] = 1.0;
            var q = new SimuLean.Serialization.ElementConfig("waiting", "UnityQueue", "waiting");
            q.Parameters["capacity"] = 100000;
            var desk = new SimuLean.Serialization.ElementConfig("delivery", "UnityMultiServer", "delivery");
            desk.Parameters["capacity"] = 5; desk.Parameters["delayStrategyType"] = "Constant"; desk.Parameters["constantValue"] = 0.1;
            desk.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessTeam] = "devices";
            desk.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessRelease] = release;
            desk.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessReleaseAt] = "return";
            var home = new SimuLean.Serialization.ElementConfig("home", "UnityDelay", "home");
            home.Parameters["delayStrategyType"] = "Constant"; home.Parameters["constantValue"] = 10.0;
            var ret = new SimuLean.Serialization.ElementConfig("return", "UnityQueue", "return");
            ret.Parameters["capacity"] = 100000;
            c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(desk); c.Elements.Add(home); c.Elements.Add(ret);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
            foreach (var (f, t) in new[] { ("src", "waiting"), ("waiting", "delivery"), ("delivery", "home"), ("home", "return"), ("return", "sink") })
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
            var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "devices", Name = "devices" };
            team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "poly1" });
            team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "poly2" });
            c.ResourceTeams.Add(team);
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            double maxInUse = 0;
            for (double t = 0.5; t <= 200; t += 0.5)
            {
                clock.AdvanceClock(t);
                maxInUse = Math.Max(maxInUse, model.Find("home").GetQueueLength());
            }
            return ((int)model.Find("sink").GetStatsInputCount(), maxInUse);
        }
        var (lent, inUse) = Run("AtElement");
        Check(lent >= 35 && lent <= 40, $"two devices for 10.1 each: about 2 patients per 10.1 over 200 ({lent})");
        Check(inUse <= 2, $"never more patients at home than devices ({inUse})");
        var (free, _) = Run("EndOfProcess");
        Check(free >= 185, $"released at the desk: no limit ({free})");
        bool failed = false;
        try
        {
            var c = new SimuLean.Serialization.SimulationConfig();
            var e = new SimuLean.Serialization.ElementConfig("d", "UnityMultiServer", "d");
            e.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessTeam] = "t";
            e.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessRelease] = "AtElement";
            e.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessReleaseAt] = "nowhere";
            c.Elements.Add(e);
            var tc = new SimuLean.Serialization.ResourceTeamConfig { Id = "t", Name = "t" };
            tc.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "m" });
            c.ResourceTeams.Add(tc);
            new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(c);
        }
        catch (FormatException) { failed = true; }
        Check(failed, "an unknown release element fails the build");
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
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "Schedule";
            src.Parameters["arrivalsTable"] = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "p" }, ["Q"] = new List<string> { "200" },
            };
            var q = new SimuLean.Serialization.ElementConfig("q", "UnityQueue", "q");
            q.Parameters["capacity"] = 100000;
            q.Parameters["outputStrategyType"] = "RoundRobin";
            c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "q", "General"));
            foreach (var n in new[] { "a", "b" })
            {
                var st = new SimuLean.Serialization.ElementConfig(n, "UnityMultiServer", n);
                st.Parameters["capacity"] = 1; st.Parameters["delayStrategyType"] = "Constant"; st.Parameters["constantValue"] = 1.0;
                c.Elements.Add(st);
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("q", n, "General"));
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(n, "sink", "General"));
                var shift = new SimuLean.Serialization.DowntimeConfig("agenda_" + n, "Shift", n);
                shift.Parameters["pattern"] = "Mon-Fri 08:00-12:00";
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

    // 100 referrals of one (a row each, every 1) — factor 1.2 gives 120, 0.5 gives 50, at the rows' times;
    // an inter-arrival source every 1 with factor 2 gives about 200 in 100; a factor of 0 fails.
    static void TestDemandFactor()
    {
        int Count(string mode, double? factor)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = mode;
            if (mode == "Schedule")
            {
                var times = Enumerable.Range(0, 100).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
                src.Parameters["arrivalsTable"] = new Dictionary<string, List<string>>
                {
                    ["Time"] = times, ["Name"] = times.Select(_ => "p").ToList(), ["Q"] = times.Select(_ => "1").ToList(),
                };
            }
            else
            {
                src.Parameters["interArrivalStrategyType"] = "Constant";
                src.Parameters["constantValue"] = 1.0;
            }
            if (factor.HasValue) src.Parameters["demandFactor"] = factor.Value;
            c.Elements.Add(src);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
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

    // Referrals every 1 go to agenda A's list while it holds fewer than 5 (routing
    // content('A list') < 5 ? 0 : 1), else to agenda B's; each agenda sees one patient per 3. A's list never exceeds 5,
    // B takes the overflow. get / free read an element's properties; an unknown element fails naming it.
    static void TestExpressionsReadElements()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 300 };
        var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
        src.Parameters["sourceMode"] = "InterArrival";
        src.Parameters["interArrivalStrategyType"] = "Constant";
        src.Parameters["constantValue"] = 1.0;
        src.Parameters["outputStrategyType"] = "Expression";
        src.Parameters["outputStrategy_expression"] = "content('A list') < 5 ? 0 : 1";
        c.Elements.Add(src);
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink"));
        foreach (var n in new[] { "A", "B" })
        {
            var q = new SimuLean.Serialization.ElementConfig(n + "q", "UnityQueue", n + " list");
            q.Parameters["capacity"] = 100000;
            var st = new SimuLean.Serialization.ElementConfig(n, "UnityMultiServer", n);
            st.Parameters["capacity"] = 1; st.Parameters["delayStrategyType"] = "Constant"; st.Parameters["constantValue"] = 3.0;
            c.Elements.Add(q); c.Elements.Add(st);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", n + "q", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(n + "q", n, "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(n, "sink", "General"));
        }
        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(300);
        var aq = model.Find("A list"); var bq = model.Find("B list");
        Check(aq.GetStatsContentMax() <= 5, $"agenda A's list never above 5 ({aq.GetStatsContentMax()})");
        Check(bq.GetStatsInputCount() > 100, $"agenda B takes the overflow ({bq.GetStatsInputCount()})");

        var scope = new SimuLean.Expressions.EntityScope().Set(null, model.Find("src"));
        Check(SimuLean.Expressions.Expression.Parse("get('A', 'name')").Evaluate(scope).ToString() == "A", "get reads an element's property");
        Check(SimuLean.Expressions.Expression.Parse("free('A list') > 99000").Evaluate(scope).AsBool(), "free: the list's room");
        string error = null;
        try { SimuLean.Expressions.Expression.Parse("content('nowhere')").Evaluate(scope); }
        catch (InvalidOperationException e) { error = e.Message; }
        Check(error != null && error.Contains("nowhere"), $"an unknown element fails naming it ({error})");
    }

    // An agenda Mon-Fri 08:00-12:00 with 1 h slots, the first of each session reserved for priority
    // patients; referrals at Monday 00:00 (the model counts hours). Ten ordinary patients get Mon 9-11,
    // Tue 9-11, Wed 9-11 and Thu 9 (times 9, 10, 11, 33…, 81); a priority one Mon 8. Overflow: with no ordinary patient a
    // second priority patient takes Mon 9 (Ordinary) or waits for Tue 8 (WaitReserved). A lead time of 24 moves the first
    // booking to Tue 9. Patients reach the consultation at their appointment; no-shows are stamped and counted.
    static void TestAgenda()
    {
        (List<Item> seen, SimuLean.Results.ResultObject stats) Run((int n, int prio)[] referrals, string overflow = "Ordinary",
                                                                   double lead = 0, string noShow = null, double until = 200)
        {
            var c = new SimuLean.Serialization.SimulationConfig
            {
                UseFixedSeed = true, Seed = 1, MaxSimulationTime = until, StartDateTime = "2025-01-06 00:00", TimeUnitSeconds = 3600,
            };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "Schedule";
            src.Parameters["arrivalsTable"] = new Dictionary<string, List<string>>
            {
                ["Time"] = referrals.Select(_ => "0").ToList(), ["Name"] = referrals.Select(_ => "p").ToList(),
                ["Q"] = referrals.Select(r => r.n.ToString()).ToList(), ["priority"] = referrals.Select(r => r.prio.ToString()).ToList(),
            };
            var ag = new SimuLean.Serialization.ElementConfig("agenda", "UnityAgenda", "agenda");
            ag.Parameters["sessions"] = "Mon-Fri 08:00-12:00";
            ag.Parameters["slotMinutes"] = 60.0;
            ag.Parameters["reservedPerSession"] = 1;
            ag.Parameters["overflow"] = overflow;
            ag.Parameters["leadDays"] = lead / 24.0;
            if (noShow != null) ag.Parameters["noShow"] = noShow;
            var visit = new SimuLean.Serialization.ElementConfig("visit", "UnitySink", "visit");
            visit.Parameters["keepItems"] = true;
            visit.Parameters["entryLabels"] = new List<string> { "seen_at = now()" };
            c.Elements.Add(src); c.Elements.Add(ag); c.Elements.Add(visit);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "agenda", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("agenda", "visit", "General"));
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(until);
            var r = SimuLean.Results.RunResults.Capture(model, "agenda");
            return (((Sink)model.Find("visit")).Kept.ToList(), r.Objects.First(o => o.Name == "agenda"));
        }

        var (seen, stats) = Run(new[] { (10, 0), (1, 1) });
        var ordinary = seen.Where(it => it.GetLabelOrDefault("priority") == 0).Select(it => it.GetLabelOrDefault("appt_time")).OrderBy(t => t).ToArray();
        Check(ordinary.SequenceEqual(new double[] { 9, 10, 11, 33, 34, 35, 57, 58, 59, 81 }), $"ordinary slots ({string.Join(", ", ordinary)})");
        Check(seen.Where(it => it.GetLabelOrDefault("priority") == 1).Select(it => it.GetLabelOrDefault("appt_time")).SequenceEqual(new double[] { 8 }),
              "the priority patient takes the reserved Mon 8");
        Check(seen.All(it => it.GetLabelOrDefault("seen_at") == it.GetLabelOrDefault("appt_time")), "each patient reaches the visit at its appointment");
        Check(seen.All(it => it.GetLabelOrDefault("lead_time") == it.GetLabelOrDefault("appt_time")), "lead time = appointment - booking (booked at 0)");
        // to t = 200 (Mon 0 … Tue 8 of week 2 minus): Mon-Fri 4 slots + next Mon 4 = 24 slots offered
        Check(stats.TryGet("slots_offered", out double offered) && offered == 24, $"slots offered ({offered})");
        Check(stats.TryGet("slots_booked", out double booked) && booked == 11, $"slots booked ({booked})");
        Check(stats.TryGet("slots_reserved_unused", out double unused) && unused == 5, $"reserved slots unused ({unused})");
        Check(stats.TryGet("lead_time_max", out double leadMax) && leadMax == 81, $"lead time max ({leadMax})");

        var (ord, _) = Run(new[] { (2, 1) }, "Ordinary");
        Check(ord.Select(it => it.GetLabelOrDefault("appt_time")).OrderBy(t => t).SequenceEqual(new double[] { 8, 9 }), "Ordinary: the second priority takes Mon 9");
        var (wait, _) = Run(new[] { (2, 1) }, "WaitReserved");
        Check(wait.Select(it => it.GetLabelOrDefault("appt_time")).OrderBy(t => t).SequenceEqual(new double[] { 8, 32 }), "Wait Reserved: Tue 8");
        var (late, _) = Run(new[] { (1, 0) }, lead: 24);
        Check(late.Count == 1 && late[0].GetLabelOrDefault("appt_time") == 33, "a lead time of 24: Tue 9");
        var (absent, absentStats) = Run(new[] { (3, 0) }, noShow: "1");
        Check(absent.All(it => it.GetLabelOrDefault("no_show") == 1) && absentStats.TryGet("no_shows", out double ns) && ns == 3, "no-shows stamped and counted");
        var (holiday, _) = Run(new[] { (4, 0) });
        Check(holiday.Select(it => it.GetLabelOrDefault("appt_time")).OrderBy(t => t).SequenceEqual(new double[] { 9, 10, 11, 33 }), "three a session (one reserved)");
    }

    // Patients booked at home walk 360 m to the visit at 1 m/s: 0.1 h (the model counts hours). Released at their slot they
    // arrive 0.1 h late; with arriveOnTime the agenda releases them 0.1 h early and they arrive at their slot.
    static void TestAgendaArriveOnTime()
    {
        List<Item> Run(bool onTime, bool busyRoom = false)
        {
            var c = new SimuLean.Serialization.SimulationConfig
            {
                UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100, StartDateTime = "2025-01-06 00:00", TimeUnitSeconds = 3600,
            };
            c.Network = new SimuLean.Serialization.NetworkConfig();
            c.Network.Nodes.Add(SimuLean.Serialization.TransportStorageConfigCodec.FormatNode("Home", 0, 0, 0));
            c.Network.Nodes.Add(SimuLean.Serialization.TransportStorageConfigCodec.FormatNode("Room", 360, 0, 0));
            c.Network.Tracks.Add(SimuLean.Serialization.TransportStorageConfigCodec.FormatTrack("Home", "Room", 360, true, SimuLean.Transport.TrackType.TravelPath));
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "Schedule";
            src.Parameters["arrivalsTable"] = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "p" }, ["Q"] = new List<string> { "3" },
            };
            src.Parameters["location"] = "Home";
            var ag = new SimuLean.Serialization.ElementConfig("agenda", "UnityAgenda", "agenda");
            ag.Parameters["sessions"] = "Mon-Fri 08:00-12:00";
            ag.Parameters["slotMinutes"] = 60.0;
            ag.Parameters["location"] = "Home";
            ag.Parameters["walkSpeed"] = 1.0;
            ag.Parameters["arriveOnTime"] = onTime;
            var visit = new SimuLean.Serialization.ElementConfig("visit", "UnitySink", "visit");
            visit.Parameters["keepItems"] = true;
            visit.Parameters["location"] = "Room";
            c.Elements.Add(src); c.Elements.Add(ag); c.Elements.Add(visit);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "agenda", "General"));
            if (busyRoom)
            {
                // one room, each visit the whole slot (1 h): busy until the next patient's slot starts
                var room = new SimuLean.Serialization.ElementConfig("room", "UnityMultiServer", "room");
                room.Parameters["capacity"] = 1; room.Parameters["delayStrategyType"] = "Constant"; room.Parameters["constantValue"] = 1.0;
                room.Parameters["location"] = "Room";
                room.Parameters["entryLabels"] = new List<string> { "seen_at = now()" };
                c.Elements.Add(room);
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("agenda", "room", "General"));
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("room", "visit", "General"));
            }
            else
            {
                visit.Parameters["entryLabels"] = new List<string> { "seen_at = now()" };
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("agenda", "visit", "General"));
            }
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(100);
            return ((Sink)model.Find("visit")).Kept.ToList();
        }
        var late = Run(false);
        Check(late.Count == 3 && late.All(it => Math.Abs(it.GetLabelOrDefault("seen_at") - it.GetLabelOrDefault("appt_time") - 0.1) < 1e-9),
              $"released at the slot: 0.1 h late ({string.Join(", ", late.Select(it => it.GetLabelOrDefault("seen_at") - it.GetLabelOrDefault("appt_time")))})");
        var onTime = Run(true);
        Check(onTime.Count == 3 && onTime.All(it => Math.Abs(it.GetLabelOrDefault("seen_at") - it.GetLabelOrDefault("appt_time")) < 1e-9),
              $"arriveOnTime: at the slot ({string.Join(", ", onTime.Select(it => it.GetLabelOrDefault("seen_at") - it.GetLabelOrDefault("appt_time")))})");
        Check(onTime.All(it => it.GetLabelOrDefault("lead_time") == it.GetLabelOrDefault("appt_time")), "the lead time is still booking → slot");
        var busy = Run(true, busyRoom: true);
        Check(busy.Count >= 2 && busy.All(it => Math.Abs(it.GetLabelOrDefault("seen_at") - it.GetLabelOrDefault("appt_time")) < 1e-9),
              $"a busy room: they set off anyway, wait at the door and go in at their slot ({string.Join(", ", busy.Select(it => it.GetLabelOrDefault("seen_at") - it.GetLabelOrDefault("appt_time")))})");
    }

    // From Wednesday a template of two 1 h slots a day (Mon-Fri 08-10) replaces four (08-12);
    // three patients cancel once each and book again; priority patients overbook Monday's session (two extras).
    static void TestAgendaTemplatesCancelOverbook()
    {
        (List<Item> seen, SimuLean.Results.ResultObject stats) Run((int n, int prio)[] referrals, Action<SimuLean.Serialization.ElementConfig> setup)
        {
            var c = new SimuLean.Serialization.SimulationConfig
            {
                UseFixedSeed = true, Seed = 1, MaxSimulationTime = 400, StartDateTime = "2025-01-06 00:00", TimeUnitSeconds = 3600,
            };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "Schedule";
            src.Parameters["arrivalsTable"] = new Dictionary<string, List<string>>
            {
                ["Time"] = referrals.Select(_ => "0").ToList(), ["Name"] = referrals.Select(_ => "p").ToList(),
                ["Q"] = referrals.Select(r => r.n.ToString()).ToList(), ["priority"] = referrals.Select(r => r.prio.ToString()).ToList(),
            };
            var ag = new SimuLean.Serialization.ElementConfig("agenda", "UnityAgenda", "agenda");
            ag.Parameters["sessions"] = "Mon-Fri 08:00-12:00";
            ag.Parameters["slotMinutes"] = 60.0;
            setup(ag);
            var visit = new SimuLean.Serialization.ElementConfig("visit", "UnitySink", "visit");
            visit.Parameters["keepItems"] = true;
            c.Elements.Add(src); c.Elements.Add(ag); c.Elements.Add(visit);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "agenda", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("agenda", "visit", "General"));
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(400);
            var r = SimuLean.Results.RunResults.Capture(model, "agenda");
            return (((Sink)model.Find("visit")).Kept.ToList(), r.Objects.First(o => o.Name == "agenda"));
        }
        double[] Appts(List<Item> seen) => seen.Select(it => it.GetLabelOrDefault("appt_time")).OrderBy(t => t).ToArray();

        var (later, _) = Run(new[] { (12, 0) }, ag => ag.Parameters["templates"] = new List<string> { "2025-01-08 | Mon-Fri 08:00-10:00 | 60" });
        Check(Appts(later).SequenceEqual(new double[] { 8, 9, 10, 11, 32, 33, 34, 35, 56, 57, 80, 81 }),
              $"four slots Mon-Tue, two from Wednesday ({string.Join(", ", Appts(later))})");

        var (moved, movedStats) = Run(new[] { (3, 0) }, ag => ag.Parameters["cancel"] = "rescheduled < 1 ? 1 : 0");
        Check(moved.Count == 3 && moved.All(it => it.GetLabelOrDefault("rescheduled") == 1), "each cancelled once and was seen");
        Check(movedStats.TryGet("cancellations", out double cancels) && cancels == 3, $"three cancellations ({cancels})");
        Check(moved.All(it => it.GetLabelOrDefault("lead_time") == it.GetLabelOrDefault("appt_time")), "lead time from the first booking");

        var (over, overStats) = Run(new[] { (4, 1) }, ag =>
        {
            ag.Parameters["reservedPerSession"] = 1; ag.Parameters["overflow"] = "Overbook"; ag.Parameters["overbookPerSession"] = 2;
        });
        Check(Appts(over).SequenceEqual(new double[] { 8, 9, 11, 11 }), $"reserved Mon 8, two extras at Mon 11, then Mon 9 ({string.Join(", ", Appts(over))})");
        Check(overStats.TryGet("overbooked", out double extras) && extras == 2, $"two extra slots ({extras})");
        // review2 #28: no reserved slots — the priority patients take the session's free slots before any extra one
        var (fill, fillStats) = Run(new[] { (4, 1) }, ag =>
        {
            ag.Parameters["reservedPerSession"] = 0; ag.Parameters["overflow"] = "Overbook"; ag.Parameters["overbookPerSession"] = 2;
        });
        Check(Appts(fill).SequenceEqual(new double[] { 8, 9, 10, 11 }), $"no reserved slots: Mon 8, 9, 10, 11 ({string.Join(", ", Appts(fill))})");
        Check(fillStats.TryGet("overbooked", out double none) && none == 0, $"no extra slot while the session has room ({none})");
        bool bad = false;
        try { Run(new[] { (1, 0) }, ag => ag.Parameters["templates"] = new List<string> { "not a date | Mon 08:00-09:00 | 60" }); }
        catch (FormatException) { bad = true; }
        Check(bad, "a malformed template fails the build");
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

    static void TestRandomExpressionSamplers()
    {
        int Arrivals(int seed)
        {
            var clock = new SimClock(seed);
            var src = new InterArrivalSource("Src", clock, new SimuLean.Expressions.ExpressionSampler("exponential(5)"));
            var sink = new Sink("Sink", clock);
            GeneralLink.CreateLink(src, L(sink));
            src.Start(); sink.Start();
            clock.AdvanceClock(500);
            return (int)sink.GetStatsInputCount();
        }
        int n = -1, again = -2;
        string error = null;
        try { n = Arrivals(7); again = Arrivals(7); } catch (Exception e) { error = e.Message; }
        Check(error == null && n > 50 && n < 150 && n == again, $"a source with exponential(5) runs and repeats for a seed ({n}, {again}, {error})");

        error = null;
        int stops = 0;
        try
        {
            var clock = new SimClock(3);
            var m = new MultiServer(new IRandomSampler[] { Const(1) }, "M", clock);
            var feed = new InterArrivalSource("Feed", clock, Const(1));
            var sink = new Sink("Sink", clock);
            GeneralLink.CreateLink(feed, L(m)); GeneralLink.CreateLink(m, L(sink));
            var gen = new SimuLean.Downtime.MtbfMttrDowntime(m, new SimuLean.Expressions.ExpressionSampler("20 + 10 * rand()"),
                                                             new SimuLean.Expressions.ExpressionSampler("exponential(2)"), clock);
            foreach (var e in new Element[] { feed, m, sink }) e.Start();
            gen.Start();
            clock.AdvanceClock(300);
            stops = m.StopCount;
        }
        catch (Exception e) { error = e.Message; }
        Check(error == null && stops > 3, $"failures / repairs drawn from expressions ({stops} stops, {error})");
    }

    static void TestAgendaReview2()
    {
        SimuLean.Serialization.SimulationConfig Config(double horizon, double unitSeconds, double every, string extra = null)
        {
            var c = new SimuLean.Serialization.SimulationConfig
            {
                UseFixedSeed = true, Seed = 1, MaxSimulationTime = horizon, StartDateTime = "2025-01-06 00:00", TimeUnitSeconds = unitSeconds,
            };
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "InterArrival";
            src.Parameters["interArrivalStrategyType"] = "Constant";
            src.Parameters["constantValue"] = every;
            var ag = new SimuLean.Serialization.ElementConfig("agenda", "UnityAgenda", "agenda");
            ag.Parameters["sessions"] = "Mon-Fri 08:00-12:00";
            ag.Parameters["slotMinutes"] = 60.0;
            var visit = new SimuLean.Serialization.ElementConfig("visit", "UnitySink", "visit");
            c.Elements.Add(src); c.Elements.Add(ag); c.Elements.Add(visit);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "agenda", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("agenda", "visit", "General"));
            return c;
        }

        // #4: day units, a referral every 30 days, 4000 days (> the 3660-day look-ahead counted from time 0)
        string error = null; double seen = 0;
        try
        {
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(Config(4000, 86400, 30));
            model.StartModel();
            clock.AdvanceClock(4000);
            seen = model.Find("visit").GetStatsInputCount();
        }
        catch (Exception e) { error = e.Message; }
        Check(error == null && seen > 120, $"bookings go on after ten years ({seen} seen, {error})");

        // #7: reading the statistics generates no slot (a capture only reads)
        {
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(Config(2000, 3600, 1000));
            model.StartModel();
            clock.AdvanceClock(1500);                                    // two referrals; slots for their first weeks only
            var agenda = (SimuLean.Healthcare.Agenda)model.Find("agenda");
            int before = agenda.GeneratedSlotCount;
            var r = SimuLean.Results.RunResults.Capture(model, "agenda");
            Check(agenda.GeneratedSlotCount == before, $"the capture generated no slot ({before} → {agenda.GeneratedSlotCount})");
            var o = r.Objects.First(x => x.Name == "agenda");
            Check(o.TryGet("slots_offered", out double offered) && offered > 0, $"the slots up to now are still counted ({offered})");
        }
    }
}
