// Combiner: input connections and order, batch and pull modes, requirements, utilisation, stops and labels.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
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

#if !OLDAPI
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
#endif

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

    // ── #91 ──────────────────────────────────────────────────────────────────

    static Item Texted(string label, string text) { var i = new Item(0); i.SetLabelText(label, text); return i; }

    static void TestPullModeTextLabels()
    {
        // Auto-update (no value) with a text key: the component of the main item's key is pulled, another is refused.
        foreach (string componentKey in new[] { "Garments", "Shoes" })
        {
            var clock = new SimClock(1);
            var comb = new Combiner(new[] { 1 }, new SimuLean.ConstantRandomProcess(1), "C", clock,
                                    pullMode: new SingleLabelStrategy("sku"));
            var sink = new Sink("K", clock);
            GeneralLink.CreateLink(comb, new List<Element> { sink });
            var main = new ReleaseSource("Main", clock); GeneralLink.CreateLink(main, new List<Element> { comb });
            var comp = new ReleaseSource("Comp", clock); GeneralLink.CreateLink(comp, new List<Element> { comb });
            foreach (var e in new Element[] { comb, sink, main, comp }) e.Start();
            main.Enqueue(Texted("sku", "Garments"));
            comp.Enqueue(Texted("sku", componentKey));
            clock.AdvanceClock(5);
            if (componentKey == "Garments") Check(sink.StatsCollector.TotalInput == 1, "a text key: the matching component completes the assembly (it waited forever)");
            else Check(sink.StatsCollector.TotalInput == 0 && comp.PendingCount == 1, "a text key: a component of another key is refused");
        }

        // Numbers stay numbers: a numeric key never matches a text spelled like it, and vice versa.
        var auto = new SingleLabelStrategy("k");
        auto.UpdateStrategy(Labelled("k", 3));
        Check(auto.Admits(null, Labelled("k", 3), null) && !auto.Admits(null, Texted("k", "3"), null), "a numeric main key matches numeric labels only");
        auto.UpdateStrategy(Texted("k", "3"));
        Check(auto.Admits(null, Texted("k", "3"), null) && !auto.Admits(null, Labelled("k", 3), null), "a text main key matches text labels only");

        // A fixed value from the configuration: "Garments" as text, "2" as the number 2 (or the text "2").
        var cfg = new ElementConfig("c", "Combiner", "c");
        cfg.Set("pullModeType", "SingleLabel");
        cfg.Set("singleLabelName", "sku");
        cfg.Set("singleLabelValue", "Garments");
        var fixedText = ElementBuildHelpers.BuildPullModeStrategy(cfg, cfg.SettingsAs<CombinerSettings>());
        Check(fixedText.Admits(null, Texted("sku", "Garments"), null) && !fixedText.Admits(null, Texted("sku", "Shoes"), null), "a fixed text value matches its text label");
        cfg.Set("singleLabelValue", "2");
        var fixedNumber = ElementBuildHelpers.BuildPullModeStrategy(cfg, cfg.SettingsAs<CombinerSettings>());
        Check(fixedNumber.Admits(null, Labelled("sku", 2), null) && !fixedNumber.Admits(null, Labelled("sku", 3), null), "a fixed numeric value matches the numeric label");

        // A main item without the key warns (the assembly admits nothing).
        var warnings = new List<string>();
        var old = SimLog.WarningSink; SimLog.WarningSink = w => warnings.Add(w);
        try { new SingleLabelStrategy("sku").UpdateStrategy(Labelled("other", 1)); }
        finally { SimLog.WarningSink = old; }
        Check(warnings.Count == 1 && warnings[0].Contains("sku"), "a main item without the key warns, naming the label");
    }

    static void TestCombinerStopAndLabels()
    {
        // Mains always there, a component every 2 (5 per assembly): the combiner stopped from 10 to 30 takes no component.
        {
            var clock = new SimClock(1);
            var main = new InterArrivalSource("Main", clock, Const(0.5));
            var parts = new InterArrivalSource("Parts", clock, Const(2));
            var qm = new ItemsQueue(1000, "QM", clock);
            var qp = new ItemsQueue(1000, "QP", clock);
            var comb = new Combiner(new[] { 5 }, Const(1), "Asm", clock);
            var outp = new Collector(clock);
            GeneralLink.CreateLink(main, L(qm)); GeneralLink.CreateLink(qm, L(comb));
            GeneralLink.CreateLink(parts, L(qp)); GeneralLink.CreateLink(qp, L(comb.GetComponentInput(0)));
            GeneralLink.CreateLink(comb, L(outp));
            foreach (var e in new Element[] { main, parts, qm, qp, comb, outp }) e.Start();
            StopToken stop = null;
            int atStop = -1, beforeResume = -1;
            Schedule(clock, 10, () => { stop = comb.Stop(new StopRequest(ElementState.STOPPED)); atStop = comb.GetComponentInput(0).GetQueueLength(); });
            Schedule(clock, 29.9, () => beforeResume = comb.GetComponentInput(0).GetQueueLength());
            Schedule(clock, 30, () => comb.Resume(stop));
            clock.AdvanceClock(80);
            Check(atStop == beforeResume, $"no component taken while stopped ({atStop} at the stop, {beforeResume} before the resume)");
            Check(outp.Got.Count >= 6, $"assemblies go on after the resume ({outp.Got.Count})");
        }
        // Requirements read from a label: a main item without the label needs the configured number, not the last item's.
        {
            var clock = new SimClock(1);
            var parts = new InterArrivalSource("Parts", clock, Const(1));
            var qm = new ItemsQueue(10, "QM", clock);
            var comb = new Combiner(new[] { 1 }, Const(0.5), "Asm", clock, updateRequirements: true, updateLabels: new List<string> { "n" });
            var outp = new Collector(clock);
            GeneralLink.CreateLink(qm, L(comb));
            GeneralLink.CreateLink(parts, L(comb.GetComponentInput(0)));
            GeneralLink.CreateLink(comb, L(outp));
            foreach (var e in new Element[] { parts, qm, comb, outp }) e.Start();
            int reqSecond = -1;
            Schedule(clock, 0.5, () => { var it = new Item(clock.GetSimulationTime()); it.SetLabelValue("n", 3.0); qm.Receive(it); });
            Schedule(clock, 20, () => { qm.Receive(new Item(clock.GetSimulationTime())); reqSecond = comb.Assemblies[0].Requirements[0]; });
            clock.AdvanceClock(40);
            Check(reqSecond == 1, $"the item without the label needs the configured 1 component ({reqSecond})");
            Check(outp.Got.Count == 2, $"both assemblies done ({outp.Got.Count})");
        }
    }
}
