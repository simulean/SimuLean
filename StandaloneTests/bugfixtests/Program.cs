// Regression tests of the fixed bugs: one test per bug.
// Each test fails on the engine before the fix and passes after it (except where noted).
// The tests are split by area into files of the partial class Program; this file holds Main, which runs them in
// order, and the harness they share.
// Run: dotnet run  (from StandaloneTests/bugfixtests)
// Build with -p:DefineConstants=OLDAPI to compile against an engine without the new APIs
// (warm-up and other newer APIs); those tests are then skipped.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimuLean;
using SimuLean.Resources;

static partial class Program
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
        // admission, culture, gates, stops, links, build errors, statistics
        Run("#22 a Condition reading element.x admits through CanAccept and the link", TestConditionReadsElement);
        Run("#23 schedule tables are read in the invariant culture", TestScheduleCulture);
        Run("#24 Combiner pull mode SingleLabel auto-update accepts the main item", TestPullModeAutoUpdate);
        Run("#25 a gated queue's release wakes the blocked upstream", TestGateReleaseWakesUpstream);
        Run("#26 a stop on a sink resumes without an exception", TestSinkStopResume);
        Run("#27 an element without links keeps its items instead of failing", TestElementWithoutLinks);
        Run("#28 model errors fail the build: unknown type, missing connection target", TestModelErrorsFail);
        Run("#29 config parameters: one conversion rule for elements and downtimes", TestConfigParameters);
        Run("#30 Combiner utilisation and service time are its own (single-job station)", TestCombinerUtilisation);
        // queue pass-through statistics
        Run("#31 an item that passes a queue without waiting counts in its stay time", TestQueuePassThroughStay);
        Run("#32 stay-time standard deviation (Welford) and the stay_std / wait_std metrics", TestStayStdDev);
        Run("#33 a sampler without a stream warns once that its draws are not reproducible", TestUnassignedStreamWarns);
        Run("#34 table files: CSV quoting, short rows, .data; the source takes a table", TestTableReader);
        Run("#35 storage retrievals are list queries over its stock: sku, labels, depth, FIFO", TestStorageStockQueries);
        // units, end conditions, deadlocks, matching and transport, agendas, routing, streams
        Run("#36 the time unit applies without a start date: t = 46 h with a 1 h unit is 2000-01-02 22:00", TestUndatedTimeUnit);
        Run("#37 team units: physical speeds (m/s) and load times (s) converted with the time unit", TestPhysicalUnits);
        Run("#42 a finished model with surplus from an unlimited source is not a deadlock; makespan into storage; end at any elements", TestFinishedWithSurplus);
        Run("#48 a deadlock through a list: the Combiners wait for their component list, the yard for the list's pullers", TestDeadlockThroughList);
        Run("#43 a Combiner holding only kits whose main supply is exhausted is starved (finished), not a deadlock; it waits for its main input", TestCombinerStarved);
        Run("#44 transport on list transfers with an Axes crane: a yard serves sub-blocks by key to 2 bays, out of order", TestYardListAxes);
        Run("#45 a stopped station does not take a shared resource for a job that cannot start", TestStoppedStationKeepsNoResource);
        Run("#46 a Multi-Server's item count stays its content: capacity 150, per-item delays, stops, blocked output", TestMultiServerCountInvariant);
        Run("#47 a Multi-Server's cost does not grow with its capacity: an unbounded delay of 100 000 slots", TestMultiServerLargeCapacity);
        Run("#49 a Multi-Server's items include the ones waiting for a resource", TestMultiServerItemsWaitingForResource);
        Run("#50 a shift session takes as many slots as fit: twelve exact 20-minute slots in 4 h, not thirteen", TestShiftSessionSlots);
        Run("#51 time-weighted content averages cover the whole run, not only the time since the first item", TestContentAverageFromRunStart);
        Run("#52 the warm-up also resets resources, teams and downtime counters", TestWarmupResetsEverything);
        Run("#53 a Multi-Server in FIFO release does not let a newly finished item overtake its blocked ones", TestMultiServerFifoNoOvertake);
        Run("#54 a gated queue reports an unblock only when it sent something, and records its BLOCKED time", TestGateQueueUnblock);
        Run("#55 the end of a stop releases every blocked item the receiver has room for, not one", TestResumeReleasesAllBlocked);
        Run("#56 one feed into the stock of two stations keeps both connections; an unknown connection type fails", TestStockFeedTwoStations);
        Run("Picking station: returnLeftovers off keeps the bins' stock between containers", TestPickingKeepsStock);
        Run("#57 a decimal comma in label values and network records is a decimal comma, not a thousands separator", TestDecimalCommaInConfigs);
        Run("#58 records that cannot be read fail the build instead of being dropped: timetable intervals, holidays, lane lengths", TestUnreadableRecordsFail);
        Run("#59 hot-path bookkeeping: slot trackers keep no log, a queue's held items are a live view", TestHotPathBookkeeping);
        Run("#60 a source restarted with Start() forgets the item it held", TestSourceRestartForgetsHeldItem);
        Run("#61 a Delay holds every item at once, each for its own time; a blocked output keeps them in finishing order", TestDelayElement);
        Run("#62 probability routing: shares by weight, drawn once per item from the source's stream, reproducible", TestProbabilityRouting);
        Run("#63 labels on entry with random draws drive a repeat loop: rand() / pick() from the element's stream", TestEntryLabelsLoop);
        Run("#64 a refused hand-over leaves the item's labels as they were; random functions need an element", TestLabelsUndoneOnRefusal);
        Run("#65 item statistics at an element: time in system, P50 / P90, a share, split by a label, in RunResults", TestItemTallies);
        Run("#66 a resource released at another element goes with the item: two devices lent out limit the patients", TestReleaseAtElement);
        Run("#67 the model's holidays close every shift: one working calendar for two agendas", TestModelHolidays);
        Run("#68 a source's demand factor scales the arrivals: table quantities with carried fractions, inter-arrival rate", TestDemandFactor);
        Run("#69 expressions read other elements: an overflow rule between two agendas by content('…'); get / free; unknown names fail", TestExpressionsReadElements);
        Run("#70 an agenda books each patient into the first free slot of its sessions and releases it at the appointment; reserved slots, overflow, lead time, no-shows, statistics", TestAgenda);
        Run("#71 an agenda's later template, cancellations booked again, overbooking for priority patients", TestAgendaTemplatesCancelOverbook);
        Run("#72 an agenda whose patients walk releases them their walk early: they arrive at their slot (arriveOnTime)", TestAgendaArriveOnTime);
        Run("#38 end conditions: a run stopped at the sink count is the run to that time; model empty; deadlock", TestRunControl);
        Run("#39 matching + transport: a yard serves bays by key with a crane — direct (any accepted), list, Combiner capacity", TestYardKitting);
        Run("#40 work points: the resources processing at a station stand at its free work nodes, not on its item point", TestWorkNodes);
        Run("#41 HeadlessModelFactory.Find: elements by display name (ids are Type_Name_instanceId), ambiguous names fail", TestFindByName);
        Run("#74 an agenda keeps booking past ten years of model time; a capture does not generate slots; a cancelled extra slot is given back", TestAgendaLongHorizonCaptureAndExtraSlot);
        Run("#73 random expressions (exponential(), rand()) as inter-arrival and failure / repair times draw from the element's stream", TestRandomExpressionSamplers);
        Run("#75 a freed place goes to the origin that has waited longest, among those the destination's input strategy admits", TestFairStart);
        Run("#77 a Combiner input with a requirement of 2 fed by a FIFO queue keeps asking it until the set is complete", TestCombinerQueueFedRequirement);
        Run("#78 utilisation of a Multi-Server / Combiner leaves out the time under a stop: the slots account the stop state", TestUtilizationUnderStop);
        Run("#79 at Idle a stop nothing will end does not hide a deadlock: the element under it is named with its stop state", TestDeadlockUnderStop);
        Run("#76 pick() evaluates each weight once; a tally's rand() leaves the element's draws alone; Overbook without reserved slots fills the session first", TestPickWeightsTallyDrawsAndOverbook);
        Run("#80 a node a crane delivers into a queue that pulls from and pushes to the same list is listed again", TestWeldQueueSameList);
        Run("#81 a night off shift is no stall: only the calendar's tick is pending, the shift starts again", TestShiftNightNoStall);
        Run("#82 Combiner: a stopped combiner's component inputs take nothing; a missing requirement label means the configured requirement", TestCombinerStopAndLabels);
        Run("#83 Queue: BLOCKED while its head cannot leave (an arrival does not hide it); utilisation = not empty", TestQueueBlockedState);
        Run("#84 End condition with a warm-up: the count starts with the observed period; a stop running at the end counts in downtime_total", TestEndCountAndRunningStop);
        Run("#85 Schedule source: a new arrival queues behind the backlog, it never overtakes held items", TestScheduleBacklogOrder);
        Run("#86 Return home: a team's members walk back to the home node after each task, free only there", TestReturnHome);
        Run("#87 Random labels: each label draws from its own stream (another random label leaves its draws as they were)", TestRandomLabelsOwnStream);
        Run("#89 a deadlock beside a calendar MTBF generator or a periodic downtime is reported at the Idle point, not at the horizon", TestDeadlockBesideBackgroundGenerators);
        Run("A deadlock names the full buffer without outputs that holds its feeder up, beside the blocked feeder", TestDeadlockNamesFullBufferAtRest);
        Run("#90 end condition 'stop when no progress for T': a deadlock beside a periodic signal ends T after the last movement; off = unchanged; a night off shift is no lack of progress", TestNoProgressEndCondition);
        Run("#91 Combiner pull mode SingleLabel matches text keys as text and numbers as numbers; a main item without the key warns", TestPullModeTextLabels);
        Run("#92 ShortestQueue routing breaks ties in turn (after the output chosen last), deterministically", TestShortestQueueTiesInTurn);
        Run("#93 an expression routing with rand() keeps an item's draw while it waits for a busy output (no redraw on each offer: the split stays 50 / 50)", TestExpressionRoutingDrawKept);
        Run("#88 Every random use of an element has its own stream: random routing, admission and labels leave the process times as they were", TestRandomUsesOwnStreams);
        Run("A label read that nothing sets (an unquoted text) is flagged; quoted texts, labels set elsewhere and the reference models are not", TestUnsetLabelReads);
        Run("The diagnosis names the real wait (a resource while processing, room downstream only when BLOCKED) and a member's task once", TestDiagnosisRealWaits);
        Run("An empties origin that empties carriers or already sends its items on is a build error naming the queue pattern", TestEmptiesOriginError);
        Run("The model description names teams and points by name, shows a team's orders and home, a repair crew, and summarises lanes", TestDescriptionNames);
        Run("A requester's default priority is shown when its team's requests differ or are ordered by priority", TestRequesterPriorityShown);
        Run("A Combiner processing waits for nothing; an unpacker reports carriers and pieces apart", TestDiagnosisCombinerAndCarriers);
        Run("A network team without a home node starts at the network's first node, and validate says so", TestDefaultHomeNode);
        Run("A queue counts the items that passed straight through apart from those its order released", TestQueuePassThroughCounter);
        Run("A repeat by a direct back-connection instead of a zero-length hop — to the input queue: same counts, needs room for the returns; to the station itself: as the hop while a slot is spare, a deadlock when full", TestRepeatBackConnection);
        Run("An AfterCurrent stop starts when the work in hand ends and lasts its whole length (job before / after the scheduled end, several jobs, idle); downtime_total = time stopped, downtime_scheduled = time planned", TestAfterCurrentDelaysTheStop);
        Run("A stop due while an AfterCurrent one waits: Allow overlaps them, Serialize / Merge and a periodic stop without overlap queue it behind", TestAfterCurrentStopDueWhileDelayed);
        Run("An AfterCurrent failure: its repair time counts from the end of the job in hand", TestAfterCurrentFailure);
        Run("A downtime's kind defaults (state, mode) are those the engine builds without them", TestDowntimeKindDefaults);

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
            it.SetId(Name, ++n, 0);
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

    sealed class At : IEvent
    {
        readonly Action a;
        public At(Action a) { this.a = a; }
        public void Execute() => a();
    }

    static void Schedule(SimClock clock, double t, Action a) => clock.ScheduleEvent(new At(a), t - clock.GetSimulationTime());

    static List<Element> L(params Element[] e) => new List<Element>(e);

    static IRandomSampler Const(double v) => new ConstantRandomProcess(v);

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

    // ── bugs and robustness ─────────────

    static Element Feed(SimClock clock, string name, Element to, params Item[] items)
    {
        var src = new ReleaseSource(name, clock);
        GeneralLink.CreateLink(src, new List<Element> { to });
        src.Start();
        src.Enqueue(items);
        return src;
    }

    static Item Labelled(string label, double value) { var i = new Item(0); i.SetLabelValue(label, value); return i; }
}
