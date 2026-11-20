// Resources: operators and teams at stations, work nodes, resources released at another element, return home.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;

static partial class Program
{
    // ── #2 ───────────────────────────────────────────────────────────────────

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

#if !OLDAPI
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

    static void TestReturnHome()
    {
        (double busy, double done, IEnumerable<string> where) Run(bool returnHome)
        {
            var cfg = SimuLean.Testing.ReferenceModels.ResourceWalkMatrix();   // two operators, home H, stations S1 and S2
            cfg.ResourceTeams[0].ReturnHome = returnHome;
            var clock = new SimClock(cfg.Seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(cfg);
            model.StartModel();
            var ops = model.Executors.Where(x => x.Name.StartsWith("Op")).ToList();
            var away = new List<string>();
            foreach (var op in ops) op.BecameAvailable += r => { if (r.Location != "H") away.Add(r.Name + "@" + r.Location); };
            clock.AdvanceClock(cfg.MaxSimulationTime);
            var r = SimuLean.Results.RunResults.Capture(model);
            double busy = ops.Average(o => o.Utilization);
            return (busy, r.Find(SimuLean.Results.ResultScope.Element, "sink")[SimuLean.Results.ResultMetrics.Input], away);
        }
        var stay = Run(false);
        var back = Run(true);
        Check(stay.where.Any(), "without the option a member becomes free away from home");
        Check(!back.where.Any(), "with it a member becomes free only at home: " + string.Join(",", back.where.Take(3)));
        Check(back.busy > stay.busy, $"the walk back is busy time ({back.busy:F3} > {stay.busy:F3})");
        // the walks back (5–8 m at 1.2) saturate the two operators: fewer parts, but the line keeps working
        Check(back.done > 100 && back.done < stay.done, $"the line keeps working, slower ({back.done} vs {stay.done} done)");
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
                var e = new SimuLean.Serialization.ElementConfig(id, "Source", id);
                e.Set("mode", "Schedule");
                e.Set("arrivalsTable", new Dictionary<string, List<string>>
                {
                    ["Time"] = new List<string>(times), ["Name"] = times.Select(_ => id).ToList(), ["Q"] = times.Select(_ => "1").ToList(),
                });
                return e;
            }
            SimuLean.Serialization.ElementConfig Station(string id, double time)
            {
                var e = new SimuLean.Serialization.ElementConfig(id, "MultiServer", id);
                e.Set("capacity", 1); e.Set("delayStrategy.strategyType", "Constant"); e.Set("delayStrategy.constantValue", time);
                e.Set("processBy.team", "staff");
                return e;
            }
            c.Elements.Add(Src("srcA", "1")); c.Elements.Add(Src("srcB", "0", "10"));
            c.Elements.Add(Station("A", 4)); c.Elements.Add(Station("B", 6));
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("outA", "Sink", "outA"));
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("outB", "Sink", "outB"));
            foreach (var (f, t) in new[] { ("srcA", "A"), ("srcB", "B"), ("A", "outA"), ("B", "outB") })
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
            var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "staff", Name = "staff" };
            team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "Specialist" });
            c.ResourceTeams.Add(team);
            var shift = new SimuLean.Serialization.DowntimeConfig("offShift", "Timetable", "A");
            shift.Set("mode", "Immediate");
            shift.Set("rows", new List<SimuLean.Serialization.DowntimeIntervalSettings>
            {
                SimuLean.Serialization.DowntimeBuilder.IntervalOf("", new SimuLean.Downtime.DowntimeInterval(3, 97, ElementState.STOPPED)),
            });
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

    static void TestWorkNodes()
    {
        SimuLean.Serialization.SimulationConfig Model(bool withWorkNodes)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var table = new Dictionary<string, List<string>> { ["Time"] = new List<string> { "0", "50" }, ["Name"] = new List<string> { "src", "src" }, ["Q"] = new List<string> { "1", "1" } };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "Schedule"); src.Set("arrivalsTable", table);
            var st = new SimuLean.Serialization.ElementConfig("st", "MultiServer", "st");
            st.Set("capacity", 1); st.Set("delayStrategy.strategyType", "Constant"); st.Set("delayStrategy.constantValue", 10.0);
            st.Set("location", "st");
            if (withWorkNodes) st.Set("workNodes", new List<string> { "w1", "w2" });
            st.Set("processBy.team", "welders");
            st.Set("processBy.requirements", new List<SimuLean.Serialization.RequirementSettings> { SimuLean.Serialization.RequirementSettings.Of(2, "", "weld") });
            var sink = new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink");
            c.Elements.Add(src); c.Elements.Add(st); c.Elements.Add(sink);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "st", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("st", "sink", "General"));
            c.TravelMatrices.Add(new SimuLean.Serialization.TravelMatrixConfig { Id = "m", Entries = new List<SimuLean.Serialization.TravelMatrixEntryConfig> { SimuLean.Serialization.TravelMatrixEntryConfig.Of("home", "st", 10), SimuLean.Serialization.TravelMatrixEntryConfig.Of("home", "w1", 12), SimuLean.Serialization.TravelMatrixEntryConfig.Of("home", "w2", 14), SimuLean.Serialization.TravelMatrixEntryConfig.Of("st", "w1", 2), SimuLean.Serialization.TravelMatrixEntryConfig.Of("st", "w2", 2), SimuLean.Serialization.TravelMatrixEntryConfig.Of("w1", "w2", 3) } });
            var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "welders", Name = "welders", Travel = SimuLean.Serialization.TravelKind.Matrix, Matrix = "m", HomeNode = "home", Speed = 1 };
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
        try { var bad = Model(true); bad.Elements[1].Set("workNodes", new List<string> { "nowhere" }); new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(bad); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "a work node that is not a location fails the build");
    }

    // Two home monitors (a team without travel) are handed out at the delivery desk and come back when the
    // patient enters the return desk after the home test (a Delay of 10). Patients arrive every 1: about two
    // patients per 10.1 time units get through; released at the end of the delivery, all of them would.
    static void TestReleaseAtElement()
    {
        (int done, double maxInUse) Run(string release)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 200 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", 1.0);
            var q = new SimuLean.Serialization.ElementConfig("waiting", "Queue", "waiting");
            q.Set("capacity", 100000);
            var desk = new SimuLean.Serialization.ElementConfig("delivery", "MultiServer", "delivery");
            desk.Set("capacity", 5); desk.Set("delayStrategy.strategyType", "Constant"); desk.Set("delayStrategy.constantValue", 0.1);
            desk.Set("processBy.team", "devices");
            desk.Set("processBy.release", release);
            desk.Set("processBy.releaseAt", "return");
            var home = new SimuLean.Serialization.ElementConfig("home", "Delay", "home");
            home.Set("delayStrategy.strategyType", "Constant"); home.Set("delayStrategy.constantValue", 10.0);
            var ret = new SimuLean.Serialization.ElementConfig("return", "Queue", "return");
            ret.Set("capacity", 100000);
            c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(desk); c.Elements.Add(home); c.Elements.Add(ret);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
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
            var e = new SimuLean.Serialization.ElementConfig("d", "MultiServer", "d");
            e.Set("processBy.team", "t");
            e.Set("processBy.release", "AtElement");
            e.Set("processBy.releaseAt", "nowhere");
            c.Elements.Add(e);
            var tc = new SimuLean.Serialization.ResourceTeamConfig { Id = "t", Name = "t" };
            tc.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "m" });
            c.ResourceTeams.Add(tc);
            new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(c);
        }
        catch (FormatException) { failed = true; }
        Check(failed, "an unknown release element fails the build");
    }
}
