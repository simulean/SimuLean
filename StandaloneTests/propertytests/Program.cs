// Standalone tests of the property model: PropertyBag (kinds, one kind per name, lazy
// dictionaries, no allocation on the numeric path, view, text, copy), SimEntity on Item / Element / Resource.
// Run: dotnet run  (from StandaloneTests/propertytests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static int Main()
    {
        Run("PropertyBag: kinds, one kind per name, remove, names, count", TestKinds);
        Run("PropertyBag: parsed text, text of every kind, view, copy", TestTextViewCopy);
        Run("PropertyBag: the numeric path does not allocate", TestNoAllocation);
        Run("SimEntity: items, elements and resources share the property model", TestEntities);
        Run("Records: every kind round-trips; bad records fail loudly", TestRecords);
        Run("Factory: element properties from config, references resolved by name (unknown → null + warning)", TestFactoryProperties);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    sealed class Station : Element
    {
        public Station(SimClock c, string n) : base(n, c) { }
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
        public override bool Unblock() => false;
    }

    static void TestKinds()
    {
        var b = new PropertyBag();
        Check(b.Count == 0 && !b.Has("x") && b.KindOf("x") == PropertyKind.None, "empty");
        b.SetDouble("x", 2.5);
        Check(b.KindOf("x") == PropertyKind.Double && b.GetDouble("x") == 2.5, "double");
        b.SetString("x", "red");
        Check(b.KindOf("x") == PropertyKind.String && !b.TryGetDouble("x", out _) && b.Count == 1, "same name, new kind replaces the old one");
        var other = new Station(new SimClock(1), "Other");
        b.SetRef("next", other);
        b.SetArray("dims", new[] { 1.0, 2.0, 3.0 });
        Check(b.GetRef("next") == other && b.GetArray<double>("dims").Length == 3, "reference and array");
        Check(b.Count == 3 && b.Names.OrderBy(n => n).SequenceEqual(new[] { "dims", "next", "x" }), "names of every kind");
        Check(b.Remove("x") && !b.Has("x") && !b.Remove("x"), "remove once");
        Check(b.GetDouble("missing", 7) == 7, "default for a missing number");
        b.SetDouble("", 1); b.SetDouble(null, 1);
        Check(b.Count == 2, "empty names ignored");
    }

    static void TestTextViewCopy()
    {
        var b = new PropertyBag();
        b.SetParsed("n", "3,5"); b.SetParsed("t", "A-12"); b.SetParsed("e", "");
        Check(b.KindOf("n") == PropertyKind.Double && b.GetDouble("n") == 3.5, "numeric text (comma) → double");
        Check(b.KindOf("t") == PropertyKind.String && !b.Has("e"), "other text → string; empty ignored");
        b.SetDouble("third", 0.1 + 0.2);
        Check(double.Parse(b.GetText("third"), System.Globalization.CultureInfo.InvariantCulture) == 0.1 + 0.2, "numbers as round-trip text");
        b.SetRef("owner", new Station(new SimClock(1), "Lathe"));
        b.SetArray("a", new[] { 1, 2 });
        Check(b.GetText("owner") == "Lathe" && b.GetText("a") == "1,2" && b.GetText("none") == null, "text of a reference / an array / nothing");
        var view = b.View;
        Check(ReferenceEquals(view, b.View), "the view is not rebuilt per call");
        Check(view.Count == 5 && view.ContainsKey("n") && view["n"] is double && (string)view["t"] == "A-12", "view: boxed values");
        b.SetDouble("late", 1);
        Check(view.ContainsKey("late") && view.Count == 6, "the view is live");
        bool threw = false; try { var _ = view["none"]; } catch (KeyNotFoundException) { threw = true; }
        Check(threw, "missing key throws like a dictionary");
        var c = new PropertyBag(); c.SetDouble("n", 9); c.CopyFrom(b);
        Check(c.GetDouble("n") == 3.5 && c.GetText("t") == "A-12" && c.Count == b.Count, "copy overwrites and adds");
    }

    static void TestNoAllocation()
    {
        var b = new PropertyBag();
        b.SetDouble("speed", 1.5);
        double sum = 0;
        for (int i = 0; i < 1000; i++) { b.SetDouble("speed", i); if (b.TryGetDouble("speed", out double v)) sum += v; }   // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++) { b.SetDouble("speed", i); if (b.TryGetDouble("speed", out double v)) sum += v; sum += b.GetDouble("speed"); }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Check(after - before == 0, $"set / get of numbers allocates nothing ({after - before} bytes)");
        var item = new Item(0);
        Check(!item.HasProperties, "an item without labels has no bag yet");
        Check(item.GetLabelValue("x") == null && item.GetLabelOrDefault("x", 4) == 4 && !item.HasLabel("x") && !item.HasProperties,
              "reading a missing label does not create the bag");
    }

    static void TestRecords()
    {
        var b = new PropertyBag();
        b.SetDouble("rate", 0.1 + 0.2); b.SetString("note", "a|b"); b.SetArray("d", new[] { 1.5, 2.0 }); b.SetArray("s", new[] { "x", "y" });
        b.SetRef("next", new Station(new SimClock(1), "Paint"));
        var recs = SimuLean.Serialization.PropertyRecords.Encode(b);
        Check(recs.Count == 5 && recs.Contains("ref|next|Paint") && recs.Contains("s|note|a|b"), "encoded (text may contain '|'; refs by name)");
        var c = new PropertyBag();
        var refs = SimuLean.Serialization.PropertyRecords.Apply(recs, c, "test");
        Check(c.GetDouble("rate") == 0.1 + 0.2 && c.GetText("note") == "a|b", "number exact, text intact");
        Check(c.GetArray<double>("d").SequenceEqual(new[] { 1.5, 2.0 }) && c.GetArray<string>("s").SequenceEqual(new[] { "x", "y" }), "arrays");
        Check(refs.Count == 1 && refs[0].name == "next" && refs[0].target == "Paint" && !c.Has("next"), "references returned to resolve later");
        bool threw = false;
        try { SimuLean.Serialization.PropertyRecords.Apply(new[] { "d|x|abc" }, c, "test"); } catch (FormatException) { threw = true; }
        Check(threw, "a bad number fails loudly");
        threw = false;
        try { SimuLean.Serialization.PropertyRecords.Apply(new[] { "q|x|1" }, c, "test"); } catch (FormatException) { threw = true; }
        Check(threw, "an unknown kind fails loudly");

        // Text arrays whose items contain ',' or '\' come back as they were
        var e = new PropertyBag();
        var items = new[] { "a,b", @"c\d", "", @"e\,f" };
        e.SetArray("t", items);
        var f2 = new PropertyBag();
        SimuLean.Serialization.PropertyRecords.Apply(SimuLean.Serialization.PropertyRecords.Encode(e), f2, "test");
        Check(f2.GetArray<string>("t") != null && f2.GetArray<string>("t").SequenceEqual(items),
              "a text array with commas and backslashes round-trips (" + string.Join(" / ", f2.GetArray<string>("t") ?? new string[0]) + ")");
    }

    static void TestFactoryProperties()
    {
        var cfg = new SimuLean.Serialization.SimulationConfig { Seed = 1, MaxSimulationTime = 10 };
        var a = new SimuLean.Serialization.ElementConfig("q1", "UnityQueue", "Buffer"); a.Parameters["capacity"] = 5;
        var b = new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "Exit");
        a.Parameters[SimuLean.Serialization.PropertyRecords.Key] = new List<string> { "d|zone|3", "s|owner|Line 2", "ref|exit|Exit", "ref|lost|Nowhere" };
        cfg.Elements.Add(a); cfg.Elements.Add(b);
        var warnings = new List<string>();
        var old = SimLog.WarningSink;
        SimLog.WarningSink = w => warnings.Add(w);
        try
        {
            var factory = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
            var built = factory.BuildModel(cfg);
            var q = built["q1"];
            Check(q.Properties.GetDouble("zone") == 3 && q.Properties.GetText("owner") == "Line 2", "numbers and texts from config");
            Check(q.Properties.GetRef("exit") == built["sink"], "reference resolved to the element named 'Exit'");
            Check(q.Properties.Has("lost") && q.Properties.GetRef("lost") == null, "unknown target: null");
            Check(warnings.Exists(w => w.Contains("Nowhere")), "with a warning");
        }
        finally { SimLog.WarningSink = old; }
    }

    static void TestEntities()
    {
        var clock = new SimClock(1);
        var item = new Item(0);
        item.SetType("Box");
        item.SetLabelValue("weight", "12.5");
        item.SetLabelText("code", "007");
        Check(item.Name == "Box" && item.GetLabelValue("weight") == 12.5 && item.GetLabelText("code") == "007", "item labels = properties");
        Check(item.Properties.KindOf("code") == PropertyKind.String && item.Labels.Count == 2, "text stays text; Labels view");
        var copy = new Item(0); copy.CopyLabelsFrom(item);
        Check(copy.GetLabelValue("weight") == 12.5 && copy.GetLabelText("code") == "007", "labels copied");

        var element = new Station(clock, "Press");
        element.Properties.SetDouble("tonnage", 250);
        Check(element.Name == "Press" && element.GetName() == "Press" && element.Properties.GetDouble("tonnage") == 250, "elements have properties");

        var op = new TaskExecutor("Welder", clock);
        op.SetLabelValue("skill", "welding");
        op.SetLabelValue("level", "3");
        Check(op.Properties.GetText("skill") == "welding" && op.GetLabelValue("level") == 3 && op.GetListText("skill") == "welding", "resource labels on the same model");
        Check(op.LabelNames.Count() == 2 && op.GetListNumber("level") == 3, "list fields read them");

        item.Properties.SetRef("carrier", op);
        Check(item.Properties.GetRef("carrier") == op && item.GetLabelText("carrier") == "Welder", "an item refers to a resource");
    }
}
