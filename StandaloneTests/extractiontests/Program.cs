// The Unity layer's extraction rules, headless (Assets/SimuLean.Unity/Serialization/Extractors/ExtractionRules.cs): a rack
// style's lane semantics, a resource team's travel, a Combiner's input order and slots.
// Run: dotnet run  (from StandaloneTests/extractiontests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Serialization;
using SimuLean.Storage;
using SimuLean.Unity;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex.GetType().Name + ": " + ex.Message); }
    }

    static int Main()
    {
        Run("Rack semantics: each style's access and SKU rule", TestRackStyles);
        Run("Rack semantics: a multi-deep conventional rack is LIFO, warned only at the style's defaults", TestConventionalMultiDeep);
        Run("Rack semantics: the access and SKU overrides replace the style's choice", TestRackOverrides);
        Run("Team travel: engine travel, estimate, matrix / walk grid use", TestTeamTravel);
        Run("Team travel: home node and member names", TestHomeAndMembers);
        Run("Combiner: listed inputs first (connected ones, once), the rest by hierarchy key", TestInputOrder);
        Run("Combiner: list inputs take their slots, connections fill the rest, missing and extra inputs", TestInputSlots);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static (AccessDiscipline access, SkuConstraint sku, string warning) Rack(StorageRackStyle style, int capacity = 1,
        RackAccessOverride access = RackAccessOverride.StyleDefault, RackSkuMix mix = RackSkuMix.StyleDefault)
    {
        ExtractionRules.RackSemantics(style, capacity, access, mix, out var a, out var s, out string w);
        return (a, s, w);
    }

    static void TestRackStyles()
    {
        var expected = new Dictionary<StorageRackStyle, (AccessDiscipline, SkuConstraint)>
        {
            [StorageRackStyle.ConventionalRack] = (AccessDiscipline.Direct, SkuConstraint.Mixed),
            [StorageRackStyle.FloorStorage] = (AccessDiscipline.Lifo, SkuConstraint.Mixed),
            [StorageRackStyle.DriveInRack] = (AccessDiscipline.Lifo, SkuConstraint.SingleSku),
            [StorageRackStyle.PushBackRack] = (AccessDiscipline.Lifo, SkuConstraint.SingleSku),
            [StorageRackStyle.CartonFlowRack] = (AccessDiscipline.Fifo, SkuConstraint.SingleSku),
        };
        Check(expected.Count == Enum.GetValues(typeof(StorageRackStyle)).Length, "every style has an expectation");
        foreach (var kv in expected)
        {
            var r = Rack(kv.Key);
            Check((r.access, r.sku) == kv.Value, $"{kv.Key}: {kv.Value}, got ({r.access}, {r.sku})");
            Check(r.warning == null, $"{kv.Key} single-deep: no warning");
        }
        // only conventional racking depends on the lane capacity
        foreach (var style in expected.Keys.Where(s => s != StorageRackStyle.ConventionalRack))
            Check((Rack(style, 4).access, Rack(style, 4).sku) == expected[style] && Rack(style, 4).warning == null, $"{style}: capacity 4 changes nothing");
    }

    static void TestConventionalMultiDeep()
    {
        var r = Rack(StorageRackStyle.ConventionalRack, 3);
        Check(r.access == AccessDiscipline.Lifo && r.sku == SkuConstraint.Mixed, "3 deep: LIFO, mixed");
        Check(r.warning != null && r.warning.Contains("3 units per lane") && r.warning.Contains("LIFO"), "warned: " + r.warning);
        Check(Rack(StorageRackStyle.ConventionalRack, 3, RackAccessOverride.Direct).warning == null, "an access override: no warning");
        Check(Rack(StorageRackStyle.ConventionalRack, 3, RackAccessOverride.Lifo).warning == null, "a LIFO override: no warning");
        Check(Rack(StorageRackStyle.ConventionalRack, 3, mix: RackSkuMix.SingleSku).warning == null, "single SKU: no warning");
        Check(Rack(StorageRackStyle.ConventionalRack, 3, mix: RackSkuMix.Mixed).warning != null, "mixed SKU override: still warned");
    }

    static void TestRackOverrides()
    {
        foreach (StorageRackStyle style in Enum.GetValues(typeof(StorageRackStyle)))
        {
            Check(Rack(style, 2, RackAccessOverride.Direct).access == AccessDiscipline.Direct, $"{style}: Direct override");
            Check(Rack(style, 2, RackAccessOverride.Fifo).access == AccessDiscipline.Fifo, $"{style}: FIFO override");
            Check(Rack(style, 2, RackAccessOverride.Lifo).access == AccessDiscipline.Lifo, $"{style}: LIFO override");
            Check(Rack(style, 2, mix: RackSkuMix.SingleSku).sku == SkuConstraint.SingleSku, $"{style}: single-SKU override");
            Check(Rack(style, 2, mix: RackSkuMix.Mixed).sku == SkuConstraint.Mixed, $"{style}: mixed override");
            Check(Rack(style, 1, RackAccessOverride.Fifo, RackSkuMix.Mixed) == (AccessDiscipline.Fifo, SkuConstraint.Mixed, null), $"{style}: both overrides");
        }
    }

    static void TestTeamTravel()
    {
        var engine = new Dictionary<TeamTravel, TravelKind>
        {
            [TeamTravel.Abstract] = TravelKind.Abstract, [TeamTravel.Network] = TravelKind.Network, [TeamTravel.Matrix] = TravelKind.Matrix,
            [TeamTravel.Axes] = TravelKind.Axes, [TeamTravel.Grid] = TravelKind.Grid, [TeamTravel.UnityPhysics] = TravelKind.External,
            [TeamTravel.Straight] = TravelKind.Straight,
        };
        Check(engine.Count == Enum.GetValues(typeof(TeamTravel)).Length, "every travel mode has an expectation");
        foreach (var kv in engine) Check(ExtractionRules.EngineTravel(kv.Key) == kv.Value, $"{kv.Key} -> {kv.Value}");
        Check(ExtractionRules.EstimateTravel(PhysicsEstimate.Grid) == TravelKind.Grid, "estimate Grid");
        Check(ExtractionRules.EstimateTravel(PhysicsEstimate.Matrix) == TravelKind.Matrix, "estimate Matrix");
        Check(ExtractionRules.EstimateTravel(PhysicsEstimate.Network) == TravelKind.Network, "estimate Network");
        foreach (TeamTravel t in Enum.GetValues(typeof(TeamTravel)))
            foreach (PhysicsEstimate e in Enum.GetValues(typeof(PhysicsEstimate)))
            {
                bool physics = t == TeamTravel.UnityPhysics;
                Check(ExtractionRules.UsesMatrix(t, e) == (t == TeamTravel.Matrix || (physics && e == PhysicsEstimate.Matrix)), $"matrix use {t}/{e}");
                Check(ExtractionRules.UsesWalkGrid(t, e) == (t == TeamTravel.Grid || (physics && e == PhysicsEstimate.Grid)), $"walk grid use {t}/{e}");
            }
    }

    static void TestHomeAndMembers()
    {
        Check(ExtractionRules.AutoHomeId("Crew_1") == "home:Crew_1", "the automatic home point's id");
        Check(ExtractionRules.HomeNode(TeamTravel.Abstract, "N1", "T") == null, "abstract: no home even with a node");
        Check(ExtractionRules.HomeNode(TeamTravel.Network, "N1", "T") == "N1", "a node is the home");
        Check(ExtractionRules.HomeNode(TeamTravel.Straight, "N1", "T") == "N1", "straight with a node: the node");
        Check(ExtractionRules.HomeNode(TeamTravel.Straight, null, "T") == "home:T", "straight without a node: the team's position");
        Check(ExtractionRules.HomeNode(TeamTravel.Network, null, "T") == null, "network without a node: none");
        Check(ExtractionRules.MemberName("Anna", "Crew", 0) == "Anna", "a member's own name");
        Check(ExtractionRules.MemberName("", "Crew", 2) == "Crew_3" && ExtractionRules.MemberName(null, "Crew", 0) == "Crew_1", "unnamed: Team_k, 1-based");
    }

    static void TestInputOrder()
    {
        var keys = new Dictionary<string, string> { ["Bodies"] = "2", ["Wheels"] = "0", ["Seats"] = "1", ["Bolts"] = "3" };
        List<string> Order(IEnumerable<string> listed, params string[] upstream)
            => ExtractionRules.InputOrder(upstream.ToList(), listed, s => keys[s]);
        Check(Order(null, "Bodies", "Wheels", "Seats").SequenceEqual(new[] { "Wheels", "Seats", "Bodies" }), "nothing listed: hierarchy order");
        Check(Order(new[] { "Bodies" }, "Bodies", "Wheels", "Seats").SequenceEqual(new[] { "Bodies", "Wheels", "Seats" }), "the listed main first");
        Check(Order(new[] { "Seats", "Bodies" }, "Bodies", "Wheels", "Seats", "Bolts").SequenceEqual(new[] { "Seats", "Bodies", "Wheels", "Bolts" }),
              "listed in their order, then the rest");
        Check(Order(new[] { "Ghost", "Bodies", null, "Bodies" }, "Bodies", "Wheels").SequenceEqual(new[] { "Bodies", "Wheels" }),
              "a listed element not connected, a null and a repeat are dropped");
        Check(Order(new[] { "Wheels" }).Count == 0, "no connection: no input");
    }

    static void TestInputSlots()
    {
        var connected = new List<string> { "A", "B" };
        Check(ExtractionRules.InputSlots(connected, 3, i => false).SequenceEqual(new[] { "A", "B", null }), "a missing third input is null");
        Check(ExtractionRules.InputSlots(connected, 3, i => i == 1).SequenceEqual(new[] { "A", null, "B" }), "a list takes input 1");
        Check(ExtractionRules.InputSlots(connected, 1, i => false).SequenceEqual(new[] { "A", "B" }), "an extra connection is appended");
        Check(ExtractionRules.InputSlots(new List<string>(), 2, i => i == 0).SequenceEqual(new string[] { null, null }), "a list main, a missing component");
        Check(ExtractionRules.InputSlots(new List<string> { "A" }, 2, i => i == 0).SequenceEqual(new[] { null, "A" }), "a list main, a connected component");
    }
}
