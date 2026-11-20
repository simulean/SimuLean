using System.Collections.Generic;
using SimuLean.Serialization;

namespace SimuLean.Testing
{
    // Reference models of the milk run: a fixed-route tugger train with tour
    // types, and a dynamic rail shuttle (free access to the queues, requests assigned before the trip, fill-then-deliver
    // with a delivery order, kinematic travel on axes, random type labels).
    public static partial class ReferenceModels
    {
        /// <summary>
        /// Milk run: one tugger delivers carriers from two depot origins to two line-side queues, each consumed
        /// by a server whose emptied carriers wait in an empties queue at the same stop; the train collects them and unloads
        /// them at the depot (a sink). Tour types A (both flows, stops S1, S2) and B (flow B only, stop S2), pattern A, B;
        /// capacity 4; leg times per leg. Hand-checked tour ends: 41, 77, 122, 186 (waits 122 → 150 for the late B
        /// carriers), 227 — see <c>StandaloneTests/milkruntests</c>.
        /// </summary>
        public static SimulationConfig MilkRunTwoTours()
        {
            var c = New(1031, 300);
            Add(c, Batches("srcA", "WagonA", (0, 6)));
            Add(c, Batches("srcB", "ToteB", (0, 6), (150, 3)));
            Add(c, Queue("orgA", 10)); Add(c, Queue("orgB", 10));
            Add(c, Queue("lineA", 2)); Add(c, Server("useA", 1, "Constant", ("constantValue", 20.0))); Add(c, Queue("mtA", 10));
            Add(c, Queue("lineB", 3)); Add(c, Server("useB", 1, "Constant", ("constantValue", 15.0))); Add(c, Queue("mtB", 10));
            Add(c, Sink("emptyDock"));
            Connect(c, "srcA", "orgA"); Connect(c, "srcB", "orgB");
            Connect(c, "lineA", "useA"); Connect(c, "useA", "mtA");
            Connect(c, "lineB", "useB"); Connect(c, "useB", "mtB");
            Team(c, "tuggers", "", "", ("Tugger1", new string[0]));
            var mr = El("milk", "MilkRun");
            mr.Set("transportBy.team", "tuggers");
            mr.Set("transportBy.loadTime", "Constant~1");
            mr.Set("transportBy.unloadTime", "Constant~2");
            mr.Set("emptiesLoadTime", "Constant~1");
            mr.Set("emptiesUnloadTime", "Constant~1");
            mr.Set("capacity", 4);
            mr.Set("emptiesDestination", "emptyDock");
            mr.Set("stops", new List<MilkRunStopSettings>
            {
                MilkRunStopSettings.Of("S1", "", new[] { "mtA" }), MilkRunStopSettings.Of("S2", "", new[] { "mtB" }),
            });
            mr.Set("flows", new List<MilkRunFlowSettings>
            {
                MilkRunFlowSettings.Of("fA", "orgA", "S1", "lineA", 2),
                MilkRunFlowSettings.Of("fB", "orgB", "S2", "lineB", 3, null, SamplerSettings.Constant(3)),
            });
            mr.Set("tourTypes", new List<MilkRunTourTypeSettings>
            {
                MilkRunTourTypeSettings.Of("A", new[] { "fA", "fB" }, new[] { "S1", "S2" }), MilkRunTourTypeSettings.Of("B", new[] { "fB" }),
            });
            mr.Set("pattern", new List<string> { "A", "B" });
            mr.Set("legs", new List<MilkRunLegSettings>
            {
                MilkRunLegSettings.Of("Depot", "S1", SamplerSettings.Constant(10)), MilkRunLegSettings.Of("S1", "S2", SamplerSettings.Constant(5)),
                MilkRunLegSettings.Of("S2", "Depot", SamplerSettings.Constant(12)), MilkRunLegSettings.Of("Depot", "S2", SamplerSettings.Constant(8)),
            });
            Add(c, mr);
            return c;
        }

        /// <summary>Schedule source releasing batches of identical items: (time, count) rows, item name <paramref name="type"/>.</summary>
        private static ElementConfig Batches(string id, string type, params (double time, int count)[] rows)
        {
            var table = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string>(), ["Name"] = new List<string>(), ["Q"] = new List<string>(),
            };
            foreach (var r in rows)
            {
                table["Time"].Add(r.time.ToString(System.Globalization.CultureInfo.InvariantCulture));
                table["Name"].Add(type);
                table["Q"].Add(r.count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            var e = El(id, "Source");
            e.Set("mode", "Schedule");
            e.Set("arrivalsTable", table);
            return e;
        }

        /// <summary>
        /// A rail shuttle on a straight track (stops E1 0, S2 10, QS 14, Q1 19, E2 26 m; axes travel 2 m/s, 1 m/s²; load /
        /// unload 1) serves two inputs (U(20, 28) → queue Q1 at node Q1, U(20, 40) → queue Q2 at node S2, capacity 5, the sources block when
        /// full), a machine (feed queue at S2, capacity 5; exponential 25; output queue at QS, capacity 5) and two deliveries
        /// (sinks E1 and E2). Type 1 / 2 / 3 = 30 / 50 / 20 % at the sources (<c>pick(…)</c> exit labels): type 1 goes to the machine,
        /// whose output becomes type 2 / 3 (40 / 60 %); type 2 to E1, type 3 to E2. The shuttle (capacity 2) fills up taking
        /// the nearest request (<c>|distance</c>), each assigned before its trip (a type 1 only if the feed queue has room
        /// counting the ones assigned), and delivers in loading order. One hour after a 10-minute warm-up.
        /// </summary>
        public static SimulationConfig MilkRunShuttleLine()
        {
            var c = New(1201, 4200);
            c.WarmupTime = 600;
            c.Network = new NetworkConfig();
            foreach (var (id, s) in new[] { ("E1", 0.0), ("S2", 10.0), ("QS", 14.0), ("Q1", 19.0), ("E2", 26.0) })
                c.Network.Nodes.Add(NetworkNodeConfig.Of(id, s, 0, 0));
            Add(c, TypedSource("In1", 20, 28));
            Add(c, TypedSource("In2", 20, 40));
            Add(c, Queue("Q1", 5)); Add(c, Queue("Q2", 5)); Add(c, Queue("Feed", 5)); Add(c, Queue("Out", 5));
            var m = Server("Machine", 1, "Exponential", ("exponentialRate", 1.0 / 25));
            m.Set("exitLabels", new List<string> { "Type = pick(0.4, 2, 0.6, 3)" });
            Add(c, m);
            foreach (var id in new[] { "D1", "D2" })
            {
                var d = Sink(id);
                d.Set("tallies", new List<string> { "time_in_system = age" });
                Add(c, d);
            }
            Connect(c, "In1", "Q1"); Connect(c, "In2", "Q2"); Connect(c, "Feed", "Machine"); Connect(c, "Machine", "Out");
            var team = new ResourceTeamConfig { Id = "shuttle", Name = "shuttle", Travel = TravelKind.Axes, HomeNode = "Q1", AxisSpeed = new double[] { 2, 2, 2 }, AxisAccel = new double[] { 1, 1, 1 } };
            team.Members.Add(new ResourceMemberConfig { Name = "Shuttle", LoadTime = 1, UnloadTime = 1 });
            c.ResourceTeams.Add(team);
            var mr = El("rail", "MilkRun");
            mr.Set("transportBy.team", "shuttle");
            mr.Set("tourMode", "Dynamic");
            mr.Set("policy", "FillThenDeliver");
            mr.Set("assignment", "BeforeTrip");
            mr.Set("originAccess", "Any");
            mr.Set("fillTimeout", -1.0);
            mr.Set("capacity", 2);
            mr.Set("depotName", "Q1");
            mr.Set("depotNode", "Q1");
            mr.Set("stops", new List<MilkRunStopSettings>
            {
                MilkRunStopSettings.Of("E1", "E1", null), MilkRunStopSettings.Of("S2", "S2", null), MilkRunStopSettings.Of("QS", "QS", null), MilkRunStopSettings.Of("E2", "E2", null),
            });
            mr.Set("pickups", new List<MilkRunPickupSettings> { MilkRunPickupSettings.Of("Q1", "Q1"), MilkRunPickupSettings.Of("S2", "Q2"), MilkRunPickupSettings.Of("QS", "Out") });
            var flows = new List<MilkRunFlowSettings>();
            foreach (var o in new[] { "Q1", "Q2" })
            {
                flows.Add(MilkRunFlowSettings.Of(o + "_feed", o, "S2", "Feed", 0, null, null, "item.Type == 1"));
                flows.Add(MilkRunFlowSettings.Of(o + "_d1", o, "E1", "D1", 0, null, null, "item.Type == 2"));
                flows.Add(MilkRunFlowSettings.Of(o + "_d2", o, "E2", "D2", 0, null, null, "item.Type == 3"));
            }
            flows.Add(MilkRunFlowSettings.Of("Out_d1", "Out", "E1", "D1", 0, null, null, "item.Type == 2"));
            flows.Add(MilkRunFlowSettings.Of("Out_d2", "Out", "E2", "D2", 0, null, null, "item.Type == 3"));
            mr.Set("flows", flows);
            mr.Set("namedOrders", new List<NamedOrderSettings> { NamedOrderSettings.Of("Nearest", "|distance"), NamedOrderSettings.Of("LoadingOrder", "|pushTime") });
            mr.Set("requestOrder", "Nearest");
            mr.Set("deliveryOrder", "LoadingOrder");
            Add(c, mr);
            return c;
        }

        private static ElementConfig TypedSource(string id, double min, double max)
        {
            var e = El(id, "Source");
            e.Set("mode", "InterArrival");
            e.Set("interArrivalDistribution.strategyType", "Uniform");
            e.Set("interArrivalDistribution.uniformMin", min);
            e.Set("interArrivalDistribution.uniformMax", max);
            e.Set("exitLabels", new List<string> { "Type = pick(0.3, 1, 0.5, 2, 0.2, 3)" });
            return e;
        }
    }
}
