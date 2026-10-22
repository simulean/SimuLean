// SimuLean engine performance benchmark. Not part of the test gate: run it by hand before and
// after a change on the hot path (Release; DOTNET_TieredCompilation=0 for steady-state numbers).
//   dotnet run -c Release --project StandaloneTests/perfbench -- ref              reference models (time, events/s, bytes/event)
//   dotnet run -c Release --project StandaloneTests/perfbench -- scale [family]   scaling families (line, arrival, overload, team, teamprio, teamdist, list, traffic, notraffic)
//   dotnet run -c Release --project StandaloneTests/perfbench -- study [reps]     Study replications vs threads
//   dotnet run -c Release --project StandaloneTests/perfbench -- mem <spec>       retained memory
//   dotnet run -c Release --project StandaloneTests/perfbench -- verify <dir>     the reference baselines still match
//   dotnet run -c Release --project StandaloneTests/perfbench -- profile <model | scale:family:n>   allocation samples by type
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using SimuLean;
using SimuLean.Headless;
using SimuLean.Optimization;
using SimuLean.Results;
using SimuLean.Serialization;
using SimuLean.Testing;

static class Program
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "ref";
        switch (mode)
        {
            case "ref": RefModels(args.Length > 1 ? args[1] : null); break;
            case "scale": Scale(args.Length > 1 ? args[1] : null); break;
            case "study": StudyBench(args.Length > 1 ? int.Parse(args[1]) : 32); break;
            case "profile": Profile(args[1]); break;
            case "mem": Mem(args[1]); break;
            case "verify":
            {
                string dir = args[1]; int bad = 0;
                foreach (var kv in ReferenceModels.All())
                {
                    var r = ReferenceModelRunner.Run(kv.Key, kv.Value);
                    var diffs = ReferenceModelRunner.Compare(System.IO.File.ReadAllText(System.IO.Path.Combine(dir, kv.Key + ".json")), r);
                    if (diffs.Count > 0) { bad++; Console.WriteLine(kv.Key + ": " + string.Join("; ", diffs.Take(3))); }
                }
                Console.WriteLine(bad == 0 ? "all reference baselines match" : bad + " models differ");
                break;
            }
            default: Console.WriteLine("modes: ref | scale [family] | study [reps] | profile <model>"); return 1;
        }
        return 0;
    }

    // ── measurement ─────────────────────────────────────────────────────────

    struct Sample { public double BuildMs, RunMs, TotalMs; public long Events, Alloc; public int G0, G1, G2; }

    static Sample RunOnce(SimulationConfig cfg)
    {
        var c = cfg.Clone();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var clock = new SimClock(c.UseFixedSeed ? (int?)c.Seed : null);
        var factory = new HeadlessModelFactory(clock, enableLogging: false);
        factory.BuildModel(c);
        factory.StartModel();
        double tb = sw.Elapsed.TotalMilliseconds;
        clock.AdvanceClock(c.MaxSimulationTime);
        double tt = sw.Elapsed.TotalMilliseconds;
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        return new Sample
        {
            BuildMs = tb, RunMs = tt - tb, TotalMs = tt, Events = clock.EventsExecuted, Alloc = a1 - a0,
            G0 = GC.CollectionCount(0) - g0, G1 = GC.CollectionCount(1) - g1, G2 = GC.CollectionCount(2) - g2,
        };
    }

    static (Sample med, int reps) Measure(SimulationConfig cfg, int minReps = 5, double minTotalMs = 300, int maxReps = 50)
    {
        RunOnce(cfg);   // warm-up (JIT, tiering)
        var list = new List<Sample>();
        double total = 0;
        while (list.Count < minReps || (total < minTotalMs && list.Count < maxReps))
        {
            var s = RunOnce(cfg);
            list.Add(s); total += s.TotalMs;
        }
        double Med(Func<Sample, double> f) { var v = list.Select(f).OrderBy(x => x).ToList(); return v[v.Count / 2]; }
        var m = new Sample
        {
            BuildMs = Med(s => s.BuildMs), RunMs = Med(s => s.RunMs), TotalMs = Med(s => s.TotalMs),
            Events = list[0].Events, Alloc = (long)Med(s => s.Alloc),
            G0 = (int)Med(s => s.G0), G1 = (int)Med(s => s.G1), G2 = (int)Med(s => s.G2),
        };
        if (list.Any(s => s.Events != m.Events)) Console.WriteLine("   (!) event count differs between reps");
        return (m, list.Count);
    }

    static void Header() =>
        Console.WriteLine($"{"model",-28} {"reps",4} {"build ms",9} {"run ms",9} {"events",10} {"Mev/s",7} {"ns/ev",7} {"alloc MB",9} {"B/ev",6} {"gc0/1/2",9}");

    static void Line(string name, Sample m, int reps)
    {
        double evs = m.Events / (m.RunMs / 1000.0);
        Console.WriteLine(string.Format(Inv, "{0,-28} {1,4} {2,9:F3} {3,9:F3} {4,10} {5,7:F2} {6,7:F0} {7,9:F2} {8,6:F0} {9,9}",
            name, reps, m.BuildMs, m.RunMs, m.Events, evs / 1e6, m.RunMs * 1e6 / Math.Max(1, m.Events),
            m.Alloc / 1048576.0, (double)m.Alloc / Math.Max(1, m.Events), $"{m.G0}/{m.G1}/{m.G2}"));
    }

    // ── 1. reference models ─────────────────────────────────────────────────

    static void RefModels(string only)
    {
        Header();
        double sumRun = 0; long sumEv = 0;
        foreach (var kv in ReferenceModels.All())
        {
            if (only != null && kv.Key != only) continue;
            try
            {
                var (m, reps) = Measure(kv.Value);
                Line(kv.Key, m, reps);
                sumRun += m.RunMs; sumEv += m.Events;
            }
            catch (Exception e) { Console.WriteLine($"{kv.Key,-28} ERROR {e.GetType().Name}: {e.Message}"); }
        }
        Console.WriteLine(string.Format(Inv, "TOTAL run {0:F1} ms, {1} events, {2:F2} Mev/s", sumRun, sumEv, sumEv / sumRun / 1000.0));
    }

    // ── 2. scaling families ─────────────────────────────────────────────────

    static readonly Dictionary<string, (Func<int, SimulationConfig> build, int[] sizes)> Families =
        new Dictionary<string, (Func<int, SimulationConfig>, int[])>
        {
            ["basic"] = (h => { var c = ReferenceModels.BasicLine(); c.MaxSimulationTime = h; return c; }, new[] { 500, 2000, 8000, 32000, 128000 }),
            ["line"] = (LineModel, new[] { 10, 50, 100, 200 }),
            ["arrival"] = (ArrivalModel, new[] { 1, 10, 100 }),
            ["overload"] = (OverloadModel, new[] { 2000, 10000, 40000, 100000 }),
            ["team"] = (n => TeamModel(n, "", ""), new[] { 10, 50, 100, 200 }),
            ["teamprio"] = (n => TeamModel(n, "|priority desc", "|utilization"), new[] { 10, 50, 100, 200 }),
            ["teamdist"] = (n => TeamModel(n, "", "|distance", spatial: true), new[] { 10, 25, 50, 100 }),
            ["list"] = (ListModel, new[] { 500, 2000, 5000, 20000 }),
            ["traffic"] = (TrafficModel, new[] { 5, 10, 20, 40 }),
            ["notraffic"] = (n => TrafficModel(n, traffic: false), new[] { 5, 10, 20, 40 }),
        };

    static void Scale(string only)
    {
        Header();
        foreach (var kv in Families)
        {
            if (only != null && kv.Key != only) continue;
            foreach (int n in kv.Value.sizes)
            {
                try
                {
                    var cfg = kv.Value.build(n);
                    var (m, reps) = Measure(cfg, minReps: 3, minTotalMs: 300, maxReps: 20);
                    Line($"{kv.Key} n={n}", m, reps);
                }
                catch (Exception e) { Console.WriteLine($"{kv.Key} n={n} ERROR {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
            }
        }
    }

    static SimulationConfig New(int seed, double horizon) => new SimulationConfig { UseFixedSeed = true, Seed = seed, MaxSimulationTime = horizon };
    static ElementConfig El(string id, string type) => new ElementConfig(id, type, id);
    static void Connect(SimulationConfig c, string a, string b) => c.Connections.Add(new ConnectionConfig(a, b, "General"));
    static ElementConfig Source(string id, double meanGap)
    {
        var e = El(id, "UnitySource");
        e.Parameters["sourceMode"] = "InterArrival";
        e.Parameters["interArrivalStrategyType"] = "Exponential";
        e.Parameters["exponentialRate"] = 1.0 / meanGap;
        return e;
    }
    static ElementConfig Queue(string id, int cap) { var e = El(id, "UnityQueue"); e.Parameters["capacity"] = cap; return e; }
    static ElementConfig Server(string id, int cap, double mean)
    {
        var e = El(id, "UnityMultiServer");
        e.Parameters["capacity"] = cap;
        e.Parameters["delayStrategyType"] = "Exponential";
        e.Parameters["exponentialRate"] = 1.0 / mean;
        return e;
    }
    static ElementConfig ConstServer(string id, int cap, double t)
    {
        var e = El(id, "UnityMultiServer");
        e.Parameters["capacity"] = cap;
        e.Parameters["delayStrategyType"] = "Constant";
        e.Parameters["constantValue"] = t;
        return e;
    }

    // Source → (Queue → MultiServer) × n → Sink; fixed horizon: events ∝ n.
    static SimulationConfig LineModel(int n)
    {
        var c = New(7, 20000);
        c.Elements.Add(Source("src", 1.0));
        string prev = "src";
        for (int i = 0; i < n; i++)
        {
            c.Elements.Add(Queue("q" + i, 50)); c.Elements.Add(Server("ms" + i, 1, 0.8));
            Connect(c, prev, "q" + i); Connect(c, "q" + i, "ms" + i); prev = "ms" + i;
        }
        c.Elements.Add(El("sink", "UnitySink")); Connect(c, prev, "sink");
        return c;
    }

    // Source with rate r → Queue → MultiServer(capacity r) → Sink: events ∝ r, model size constant.
    static SimulationConfig ArrivalModel(int rate)
    {
        var c = New(8, 100000.0 / rate * 10);
        c.Elements.Add(Source("src", 1.0 / rate));
        c.Elements.Add(Queue("q", 100000)); c.Elements.Add(Server("ms", rate, 0.8));
        c.Elements.Add(El("sink", "UnitySink"));
        Connect(c, "src", "q"); Connect(c, "q", "ms"); Connect(c, "ms", "sink");
        return c;
    }

    // Overloaded queue (arrivals every 1, service 2): the queue grows to ~horizon/2 items.
    static SimulationConfig OverloadModel(int horizon)
    {
        var c = New(9, horizon);
        c.Elements.Add(Source("src", 1.0));
        c.Elements.Add(Queue("q", 10000000)); c.Elements.Add(ConstServer("ms", 1, 2.0));
        c.Elements.Add(El("sink", "UnitySink"));
        Connect(c, "src", "q"); Connect(c, "q", "ms"); Connect(c, "ms", "sink");
        return c;
    }

    // n stations (own source + queue each) served by a team of n/2 operators (utilisation ~0.8 of the team).
    static SimulationConfig TeamModel(int n, string requestOrder, string resourceOrder, bool spatial = false)
    {
        int members = Math.Max(1, n / 2);
        double horizon = 200000.0 / n;     // ~ constant number of jobs per size × n … keep events comparable
        horizon = 4000;
        var c = New(11, horizon);
        if (spatial)
        {
            c.Network = new NetworkConfig();
            for (int i = 0; i < n; i++)
            {
                c.Network.Nodes.Add(TransportStorageConfigCodec.FormatNode("n" + i, i * 5.0, 0, 0));
                if (i > 0) c.Network.Tracks.Add(TransportStorageConfigCodec.FormatTrack("n" + (i - 1), "n" + i, 5.0, true, SimuLean.Transport.TrackType.TravelPath));
            }
        }
        c.Elements.Add(El("sink", "UnitySink"));
        for (int i = 0; i < n; i++)
        {
            var src = Source("src" + i, 2.0);
            src.Parameters["assignLabels"] = new List<string> { "prio=" + (i % 5) };
            c.Elements.Add(src);
            c.Elements.Add(Queue("q" + i, 1000));
            var st = Server("st" + i, 1, 0.8);
            if (spatial) st.Parameters["location"] = "n" + i;
            st.Parameters[ResourceUseKeys.ProcessTeam] = "team";
            st.Parameters[ResourceUseKeys.ProcessRelease] = "EndOfProcess";
            if (requestOrder.Contains("priority")) st.Parameters[ResourceUseKeys.ProcessPriority] = "prio";
            c.Elements.Add(st);
            Connect(c, "src" + i, "q" + i); Connect(c, "q" + i, "st" + i); Connect(c, "st" + i, "sink");
        }
        var t = new ResourceTeamConfig { Id = "team", Name = "team", RequestOrder = requestOrder, ResourceOrder = resourceOrder };
        if (spatial) { t.Travel = "Network"; t.Speed = 500; t.HomeNode = "n0"; }
        for (int i = 0; i < members; i++) t.Members.Add(new ResourceMemberConfig { Name = "op" + i });
        c.ResourceTeams.Add(t);
        return c;
    }

    // A list that accumulates entries: 4 kinds pushed (1 arrival/time unit each), 4 servers pull "kind == k|age desc"
    // at 0.8/time unit each: the list grows by ~0.8 per time unit → size ~ 0.8·horizon at the end.
    static SimulationConfig ListModel(int horizon)
    {
        var c = New(12, horizon);
        c.Elements.Add(Queue("q", 10000000));
        c.Elements.Add(El("sink", "UnitySink"));
        c.Lists.Add(new ListConfig { Id = "work", Name = "work" });
        for (int k = 1; k <= 4; k++)
        {
            var s = Source("src" + k, 1.0);
            s.Parameters["assignLabels"] = new List<string> { "kind=" + k };
            c.Elements.Add(s); Connect(c, "src" + k, "q");
            c.Elements.Add(ConstServer("srv" + k, 1, 1.25));
            Connect(c, "srv" + k, "sink");
            var pull = new ConnectionConfig("work", "srv" + k, "PullFromList");
            pull.Parameters["query"] = "kind == " + k + "|age desc";
            c.Connections.Add(pull);
        }
        c.Connections.Add(new ConnectionConfig("q", "work", "PushToList"));
        return c;
    }

    // A ring network of 2v nodes (10 m apart); v origins on the even nodes send to a dock at n0, carried by v AGVs
    // (traffic on, nearest free vehicle). Load per vehicle kept roughly constant.
    static SimulationConfig TrafficModel(int v) => TrafficModel(v, true);
    static SimulationConfig TrafficModel(int v, bool traffic)
    {
        int nodes = 2 * v;
        var c = New(13, 3000);
        c.Network = new NetworkConfig();
        double r = 10.0 * nodes / (2 * Math.PI);
        for (int i = 0; i < nodes; i++)
        {
            double a = 2 * Math.PI * i / nodes;
            c.Network.Nodes.Add(TransportStorageConfigCodec.FormatNode("n" + i, r * Math.Cos(a), 0, r * Math.Sin(a)));
            c.Network.Tracks.Add(TransportStorageConfigCodec.FormatTrack("n" + i, "n" + ((i + 1) % nodes), 10.0, true, SimuLean.Transport.TrackType.TravelPath));
        }
        var dock = Queue("dock", 100000); dock.Parameters["location"] = "n0";
        c.Elements.Add(dock); c.Elements.Add(El("sink", "UnitySink")); Connect(c, "dock", "sink");
        for (int i = 1; i <= v; i++)
        {
            string node = "n" + ((2 * i - 1) % nodes);
            c.Elements.Add(Source("src" + i, 4.0 * v));   // each vehicle ~ one move every 4·v/v… keep total rate ∝ v
            var q = Queue("o" + i, 100000); q.Parameters["location"] = node;
            q.Parameters[ResourceUseKeys.TransportTeam] = "fleet";
            c.Elements.Add(q);
            Connect(c, "src" + i, "o" + i); Connect(c, "o" + i, "dock");
        }
        // total arrival rate = v / (4v) per unit... scale it: one arrival per 40/v time units per origin
        foreach (var e in c.Elements.Where(e => e.Id.StartsWith("src"))) e.Parameters["exponentialRate"] = 1.0 / (6.0 * v);
        var t = new ResourceTeamConfig { Id = "fleet", Name = "fleet", ResourceOrder = "|distance", Travel = "Network", Speed = 2.0, HomeNode = "n0", Traffic = traffic };
        for (int i = 1; i <= v; i++) t.Members.Add(new ResourceMemberConfig { Name = "agv" + i, LoadTime = 1, UnloadTime = 1 });
        c.ResourceTeams.Add(t);
        return c;
    }

    // ── 3. Study speed-up ───────────────────────────────────────────────────

    static void StudyBench(int reps)
    {
        foreach (var (name, cfg) in new[] { ("line50", LineModel(50)), ("team50", TeamModel(50, "|priority desc", "|utilization")) })
        {
            Console.WriteLine($"Study '{name}', {reps} replications, Server GC = {System.Runtime.GCSettings.IsServerGC}, cores = {Environment.ProcessorCount}");
            double t1 = 0;
            foreach (int threads in new[] { 1, 2, 4, 8, 12, 16 })
            {
                var times = new List<double>();
                long alloc = 0;
                for (int rep = 0; rep < (threads == 1 ? 2 : 3); rep++)
                {
                    var study = new Study(cfg) { Threads = threads, RecordStatistics = false };
                    study.WithReplications(reps).WithSeed(1).Metric("out", "sink.input");
                    GC.Collect();
                    long a0 = GC.GetTotalAllocatedBytes(true);
                    var sw = Stopwatch.StartNew();
                    var rs = study.Run();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    alloc = GC.GetTotalAllocatedBytes(true) - a0;
                    if (rs.Runs.Any(r => r.Error != null)) Console.WriteLine("  error: " + rs.Runs.First(r => r.Error != null).Error);
                }
                times.Sort();
                double med = times[times.Count / 2];
                if (threads == 1) t1 = med;
                Console.WriteLine(string.Format(Inv, "  threads {0,2}: {1,8:F0} ms  speed-up {2,5:F2}  efficiency {3,4:P0}  alloc {4:F0} MB",
                    threads, med, t1 / med, t1 / med / threads, alloc / 1048576.0));
            }
        }
    }

    // Live memory kept by a model after its run (retention: sinks, statistics…).
    static void Mem(string what)
    {
        var p = what.Split(':');
        foreach (var n in p[2].Split(','))
        {
            var c = Families[p[1]].build(int.Parse(n));
            long before = GC.GetTotalMemory(true);
            var clock = new SimClock(c.Seed);
            var factory = new HeadlessModelFactory(clock, enableLogging: false);
            factory.BuildModel(c); factory.StartModel();
            clock.AdvanceClock(c.MaxSimulationTime);
            long after = GC.GetTotalMemory(true);
            var sink = factory.Find("sink");
            var f = typeof(SimuLean.Statistics.ElementStatsCollector).GetField("_entryTimes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var dict = f?.GetValue(sink.StatsCollector) as System.Collections.ICollection;
            Console.WriteLine(string.Format(Inv, "{0} n={1}: live after run {2:F1} MB, events {3}, sink entries kept in _entryTimes = {4}, sink input = {5}",
                p[1], n, (after - before) / 1048576.0, clock.EventsExecuted, dict?.Count, sink.StatsCollector.VarInput.Value));
            GC.KeepAlive(factory);
        }
    }

    // ── 4. profile (allocation sampling) ───────────────────

    static void Profile(string what)
    {
        SimulationConfig cfg;
        if (what.StartsWith("scale:"))
        {
            var p = what.Split(':');
            cfg = Families[p[1]].build(int.Parse(p[2]));
        }
        else cfg = ReferenceModels.All().First(m => m.Key == what).Value;
        RunOnce(cfg);   // warm-up
        using var listener = new AllocListener();
        var s = RunOnce(cfg);
        listener.Dispose();
        Console.WriteLine(string.Format(Inv, "{0}: total {1:F1} ms (build {2:F1}), {3} events, alloc {4:F1} MB", what, s.TotalMs, s.BuildMs, s.Events, s.Alloc / 1048576.0));
        Console.WriteLine("-- allocation samples (~100 KB each) by type");
        long totalS = listener.Counts.Values.Sum();
        foreach (var kv in listener.Counts.OrderByDescending(k => k.Value).Take(20))
            Console.WriteLine(string.Format(Inv, "  {0,-80} {1,6} {2,6:P1}", kv.Key, kv.Value, (double)kv.Value / totalS));
    }

    // Samples GCAllocationTick (every ~100 KB allocated) with the allocated type name: an in-process allocation profile.
    sealed class AllocListener : EventListener
    {
        public readonly Dictionary<string, long> Counts = new Dictionary<string, long>();
        private bool on;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") { EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1); on = true; }
        }
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick")) return;
            int i = e.PayloadNames.IndexOf("TypeName");
            string t = i >= 0 ? e.Payload[i] as string : "?";
            lock (Counts) { Counts.TryGetValue(t ?? "?", out long n); Counts[t ?? "?"] = n + 1; }
        }
    }
}
