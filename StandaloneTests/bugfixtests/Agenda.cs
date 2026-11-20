// Agendas (healthcare appointments): booking into slots, templates, cancellations, overbooking, arrival on time.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
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

    static void TestPickWeightsTallyDrawsAndOverbook()
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
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", 1.0);
            var sink = new SimuLean.Serialization.ElementConfig("done", "Sink", "done");
            sink.Set("entryLabels", new List<string> { "x = rand()" });
            var tallies = new List<string> { "x_seen = x" };
            if (tallyDraws) tallies.Add("noise = rand()");
            sink.Set("tallies", tallies);
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
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "Schedule");
            src.Set("arrivalsTable", new Dictionary<string, List<string>>
            {
                ["Time"] = referrals.Select(_ => "0").ToList(), ["Name"] = referrals.Select(_ => "p").ToList(),
                ["Q"] = referrals.Select(r => r.n.ToString()).ToList(), ["priority"] = referrals.Select(r => r.prio.ToString()).ToList(),
            });
            var ag = new SimuLean.Serialization.ElementConfig("agenda", "Agenda", "agenda");
            ag.Set("sessions", "Mon-Fri 08:00-12:00");
            ag.Set("slotMinutes", 60.0);
            ag.Set("reservedPerSession", 1);
            ag.Set("overflow", overflow);
            ag.Set("leadDays", lead / 24.0);
            if (noShow != null) ag.Set("noShow", noShow);
            var visit = new SimuLean.Serialization.ElementConfig("visit", "Sink", "visit");
            visit.Set("keepItems", true);
            visit.Set("entryLabels", new List<string> { "seen_at = now()" });
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
            c.Network.Nodes.Add(SimuLean.Serialization.NetworkNodeConfig.Of("Home", 0, 0, 0));
            c.Network.Nodes.Add(SimuLean.Serialization.NetworkNodeConfig.Of("Room", 360, 0, 0));
            c.Network.Tracks.Add(SimuLean.Serialization.NetworkTrackConfig.Of("Home", "Room", 360, true, SimuLean.Transport.TrackType.TravelPath));
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "Schedule");
            src.Set("arrivalsTable", new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "p" }, ["Q"] = new List<string> { "3" },
            });
            src.Set("location", "Home");
            var ag = new SimuLean.Serialization.ElementConfig("agenda", "Agenda", "agenda");
            ag.Set("sessions", "Mon-Fri 08:00-12:00");
            ag.Set("slotMinutes", 60.0);
            ag.Set("location", "Home");
            ag.Set("walkBy.speed", 1.0);
            ag.Set("arriveOnTime", onTime);
            var visit = new SimuLean.Serialization.ElementConfig("visit", "Sink", "visit");
            visit.Set("keepItems", true);
            visit.Set("location", "Room");
            c.Elements.Add(src); c.Elements.Add(ag); c.Elements.Add(visit);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "agenda", "General"));
            if (busyRoom)
            {
                // one room, each visit the whole slot (1 h): busy until the next patient's slot starts
                var room = new SimuLean.Serialization.ElementConfig("room", "MultiServer", "room");
                room.Set("capacity", 1); room.Set("delayStrategy.strategyType", "Constant"); room.Set("delayStrategy.constantValue", 1.0);
                room.Set("location", "Room");
                room.Set("entryLabels", new List<string> { "seen_at = now()" });
                c.Elements.Add(room);
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("agenda", "room", "General"));
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("room", "visit", "General"));
            }
            else
            {
                visit.Set("entryLabels", new List<string> { "seen_at = now()" });
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
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "Schedule");
            src.Set("arrivalsTable", new Dictionary<string, List<string>>
            {
                ["Time"] = referrals.Select(_ => "0").ToList(), ["Name"] = referrals.Select(_ => "p").ToList(),
                ["Q"] = referrals.Select(r => r.n.ToString()).ToList(), ["priority"] = referrals.Select(r => r.prio.ToString()).ToList(),
            });
            var ag = new SimuLean.Serialization.ElementConfig("agenda", "Agenda", "agenda");
            ag.Set("sessions", "Mon-Fri 08:00-12:00");
            ag.Set("slotMinutes", 60.0);
            setup(ag);
            var visit = new SimuLean.Serialization.ElementConfig("visit", "Sink", "visit");
            visit.Set("keepItems", true);
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

        var (later, _) = Run(new[] { (12, 0) }, ag => ag.Set("templates", new List<string> { "2025-01-08 | Mon-Fri 08:00-10:00 | 60" }));
        Check(Appts(later).SequenceEqual(new double[] { 8, 9, 10, 11, 32, 33, 34, 35, 56, 57, 80, 81 }),
              $"four slots Mon-Tue, two from Wednesday ({string.Join(", ", Appts(later))})");

        var (moved, movedStats) = Run(new[] { (3, 0) }, ag => ag.Set("cancel", "rescheduled < 1 ? 1 : 0"));
        Check(moved.Count == 3 && moved.All(it => it.GetLabelOrDefault("rescheduled") == 1), "each cancelled once and was seen");
        Check(movedStats.TryGet("cancellations", out double cancels) && cancels == 3, $"three cancellations ({cancels})");
        Check(moved.All(it => it.GetLabelOrDefault("lead_time") == it.GetLabelOrDefault("appt_time")), "lead time from the first booking");

        var (over, overStats) = Run(new[] { (4, 1) }, ag =>
        {
            ag.Set("reservedPerSession", 1); ag.Set("overflow", "Overbook"); ag.Set("overbookPerSession", 2);
        });
        Check(Appts(over).SequenceEqual(new double[] { 8, 9, 11, 11 }), $"reserved Mon 8, two extras at Mon 11, then Mon 9 ({string.Join(", ", Appts(over))})");
        Check(overStats.TryGet("overbooked", out double extras) && extras == 2, $"two extra slots ({extras})");
        // #28: no reserved slots — the priority patients take the session's free slots before any extra one
        var (fill, fillStats) = Run(new[] { (4, 1) }, ag =>
        {
            ag.Set("reservedPerSession", 0); ag.Set("overflow", "Overbook"); ag.Set("overbookPerSession", 2);
        });
        Check(Appts(fill).SequenceEqual(new double[] { 8, 9, 10, 11 }), $"no reserved slots: Mon 8, 9, 10, 11 ({string.Join(", ", Appts(fill))})");
        Check(fillStats.TryGet("overbooked", out double none) && none == 0, $"no extra slot while the session has room ({none})");
        bool bad = false;
        try { Run(new[] { (1, 0) }, ag => ag.Set("templates", new List<string> { "not a date | Mon 08:00-09:00 | 60" })); }
        catch (FormatException) { bad = true; }
        Check(bad, "a malformed template fails the build");
    }

    static void TestAgendaLongHorizonCaptureAndExtraSlot()
    {
        SimuLean.Serialization.SimulationConfig Config(double horizon, double unitSeconds, double every, string extra = null)
        {
            var c = new SimuLean.Serialization.SimulationConfig
            {
                UseFixedSeed = true, Seed = 1, MaxSimulationTime = horizon, StartDateTime = "2025-01-06 00:00", TimeUnitSeconds = unitSeconds,
            };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", every);
            var ag = new SimuLean.Serialization.ElementConfig("agenda", "Agenda", "agenda");
            ag.Set("sessions", "Mon-Fri 08:00-12:00");
            ag.Set("slotMinutes", 60.0);
            var visit = new SimuLean.Serialization.ElementConfig("visit", "Sink", "visit");
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
