using System;
using System.Collections.Generic;
using SimuLean.Serialization;

namespace SimuLean.Testing
{
    /// <summary>
    /// Reference models of the regression net. Each is a small, single-purpose
    /// <see cref="SimulationConfig"/> built with the same DTOs and handler type names the scene
    /// extractor emits, so one definition serves the headless runner
    /// (<c>StandaloneTests/referencemodels</c>), the Unity EditMode tests and, later, a scene
    /// generator. Every model has a fixed seed and horizon: results are deterministic and are
    /// compared with the stored baselines by <see cref="ReferenceModelRunner"/>.
    /// Engine-pure (no Unity types).
    /// </summary>
    public static class ReferenceModels
    {
        /// <summary>All reference models, by name (stable order).</summary>
        public static IReadOnlyList<KeyValuePair<string, SimulationConfig>> All()
        {
            return new List<KeyValuePair<string, SimulationConfig>>
            {
                Pair("basic_line", BasicLine()),
                Pair("multiserver_3", MultiServer3()),
                Pair("routing_round_robin", RoutingRoundRobin()),
                Pair("operator_pool", OperatorPoolModel()),
                Pair("combiner", CombinerModel()),
                Pair("picking_station", PickingModel()),
                Pair("assembly_station", AssemblyModel()),
                Pair("stacker", StackerModel()),
                Pair("transport", TransportModel()),
                Pair("downtime_mtbf", DowntimeModel()),
                Pair("warmup", WarmupModel()),
                Pair("list_priority_dispatch", ListPriorityDispatch()),
                Pair("list_filtered_pull", ListFilteredPull()),
                Pair("list_combiner", ListCombiner()),
                Pair("resource_shared_team", ResourceSharedTeam()),
                Pair("resource_skill_query", ResourceSkillQuery()),
                Pair("resource_transport", ResourceTransport()),
                Pair("resource_count_two", ResourceCountTwo()),
                Pair("resource_request_priority", ResourceRequestPriority()),
                Pair("resource_fleet_nearest", ResourceFleetNearest()),
                Pair("resource_walk_matrix", ResourceWalkMatrix()),
                Pair("resource_robot_cell", ResourceRobotCell()),
                Pair("resource_gantry_stacker", ResourceGantryStacker()),
                Pair("resource_walk_grid", ResourceWalkGrid()),
                Pair("resource_walk_calibrated", ResourceWalkCalibrated()),
                Pair("resource_agv_traffic", ResourceAgvTraffic()),
                Pair("length_limited_lanes", LengthLimitedLanes()),
                Pair("shift_downtime", ShiftModel()),
                Pair("end_sink_count", EndAtSinkCount()),
                Pair("end_when_empty", EndWhenEmpty()),
                Pair("stall_combiner", StallCombiner()),
                Pair("deadlock_dead_end", DeadlockDeadEnd()),
                Pair("finish_with_surplus", FinishWithSurplus()),
                Pair("end_at_storage", EndAtStorage()),
                Pair("combiner_starved", CombinerStarved()),
                Pair("yard_list_axes", YardListAxes()),
                Pair("yard_kitting", YardKitting()),
                Pair("yard_kitting_list", YardKittingList()),
                Pair("yard_kitting_capacity", YardKittingCapacity()),
                Pair("erection_sequence", ErectionSequence()),
                Pair("erection_precedence", ErectionPrecedence()),
                Pair("naval_block_process", NavalBlockProcess()),
                Pair("walking_patients", WalkingPatients()),
            };
        }

        // ── naval library (N2) ───────────────────────────────────────────────

        // blocks with their labels: (block, seq, after, weight); arrival every `gap` in the given order
        private static ElementConfig BlockSource(string id, double gap, params (int block, int seq, string after, double weight)[] blocks)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var table = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string>(), ["Name"] = new List<string>(), ["Q"] = new List<string>(), ["block"] = new List<string>(),
                ["erect_seq"] = new List<string>(), ["erect_after"] = new List<string>(), ["weight"] = new List<string>(),
            };
            for (int i = 0; i < blocks.Length; i++)
            {
                table["Time"].Add((i * gap).ToString(inv)); table["Name"].Add("Block"); table["Q"].Add("1");
                table["block"].Add(blocks[i].block.ToString(inv)); table["erect_seq"].Add(blocks[i].seq.ToString(inv));
                table["erect_after"].Add(blocks[i].after); table["weight"].Add(blocks[i].weight.ToString(inv));
            }
            var e = El(id, "UnitySource");
            e.Parameters["sourceMode"] = "Schedule";
            e.Parameters["arrivalsTable"] = table;
            return e;
        }

        /// <summary>
        /// Erection in sequence: six blocks arrive out of order into a yard that releases any accepted item; the
        /// slipway takes only the next of the sequence (one front), the erection time grows with the block's weight, the
        /// crane is held only for its lift (`processHoldFor`); the complete hull leaves as one ship to a launch sink that
        /// keeps it.
        /// </summary>
        public static SimulationConfig ErectionSequence()
        {
            var c = New(1044, 500);
            Add(c, BlockSource("blocks", 3, (3, 3, "", 40), (1, 1, "", 55), (2, 2, "", 50), (5, 5, "", 45), (4, 4, "", 60), (6, 6, "", 35)));
            var yard = Queue("yard", 10);
            yard.Parameters[ElementBuildHelpers.ReleaseModeKey] = "AnyAccepted";
            Add(c, yard);
            var dock = El("slipway", "UnityErectionDock");
            dock.Parameters["capacity"] = 1;
            dock.Parameters["erectionRule"] = "Sequence";
            dock.Parameters["expectedBlocks"] = 6;
            dock.Parameters["delayStrategyType"] = "Expression";
            dock.Parameters["expression"] = "2 + 0.05 * weight";
            Add(c, dock);
            var launch = Sink("launch");
            launch.Parameters["keepItems"] = true;
            Add(c, launch);
            Connect(c, "blocks", "yard"); Connect(c, "yard", "slipway"); Connect(c, "slipway", "launch");
            Team(c, "crane", "", "", ("Crane", new string[0]));
            ProcessBy(c, "slipway", "crane", "EndOfProcess", "");
            c.Elements.Find(x => x.Id == "slipway").Parameters[ResourceUseKeys.ProcessHoldFor] = "Constant~1";
            return c;
        }

        /// <summary>
        /// Erection by precedence: block 1 first, 2 and 3 on it, 4 on 2, 5 on 3, 6 on 4 and 5; two erection
        /// fronts, a constant erection time and an erection crew of two; no launch — the hull stays in the dock (terminal:
        /// the makespan is its last erection).
        /// </summary>
        public static SimulationConfig ErectionPrecedence()
        {
            var c = New(1045, 500);
            Add(c, BlockSource("blocks", 2, (6, 6, "4 5", 30), (5, 5, "3", 30), (2, 2, "1", 30), (4, 4, "2", 30), (3, 3, "1", 30), (1, 1, "", 30)));
            var yard = Queue("yard", 10);
            yard.Parameters[ElementBuildHelpers.ReleaseModeKey] = "AnyAccepted";
            Add(c, yard);
            var dock = El("dock", "UnityErectionDock");
            dock.Parameters["capacity"] = 2;
            dock.Parameters["erectionRule"] = "Precedence";
            dock.Parameters["delayStrategyType"] = "Constant";
            dock.Parameters["constantValue"] = 6.0;
            Add(c, dock);
            Connect(c, "blocks", "yard"); Connect(c, "yard", "dock");
            Team(c, "crew", "", "", ("Fitter1", new string[0]), ("Fitter2", new string[0]), ("Fitter3", new string[0]));
            ProcessBy(c, "dock", "crew", "EndOfProcess", "", Requirement(2, "", ""));
            return c;
        }

        /// <summary>A generic sample hull for the naval library (no yard data): 4 blocks, 7 sub-blocks, block 1 first, 2 and 3
        /// on it, 4 on both; the work content per stage in hours (t_sba, t_ba).</summary>
        public static SimuLean.Naval.BlockBreakdown SampleHull()
        {
            string[] cols = { "block", "sub", "kind", "turn", "length", "breadth", "depth", "weight", "erect_seq", "erect_after",
                              "hull_x", "hull_y", "hull_z", "t_sba", "t_ba" };
            string[][] rows =
            {
                new[] { "1", "1", "flat",   "0", "12", "16", "1.6", "40", "1", "",    "0",  "0",  "0", "6", "16" },
                new[] { "1", "2", "flat",   "0", "12", "16", "1.6", "38", "1", "",    "0",  "0",  "0", "6", "16" },
                new[] { "2", "1", "curved", "1", "12", "8",  "5",   "55", "2", "1",   "12", "4",  "0", "8", "20" },
                new[] { "2", "2", "curved", "0", "12", "8",  "5",   "50", "2", "1",   "12", "4",  "0", "7", "20" },
                new[] { "3", "1", "curved", "1", "12", "8",  "5",   "54", "3", "1",   "12", "-4", "0", "8", "18" },
                new[] { "4", "1", "flat",   "0", "12", "16", "2",   "45", "4", "2 3", "24", "0",  "5", "6", "16" },
                new[] { "4", "2", "flat",   "0", "12", "16", "2",   "44", "4", "2 3", "24", "0",  "5", "6", "16" },
            };
            var table = new Dictionary<string, List<string>>();
            for (int c = 0; c < cols.Length; c++)
            {
                var col = new List<string>();
                foreach (var r in rows) col.Add(r[c]);
                table[cols[c]] = col;
            }
            return SimuLean.Naval.BlockBreakdown.FromTable(table);
        }

        /// <summary>
        /// The naval library's block process (N3, the IJPR 2025 flow on existing elements, generic data): sub-block kits ->
        /// sub-assembly (2 cells, fitters) -> turning when the sub-block is turned -> sub-block yard (any accepted) -> block
        /// assembly bays (Combiner, 2 in parallel, block = its nsub sub-blocks, welders) -> outfitting -> blasting and painting
        /// (the painter for 6 of its 10 h: processHoldFor) -> block yard -> slipway (sequence, crane for the lift only) ->
        /// launch (keeps the ship). The tables are the sample hull's generated parts.
        /// </summary>
        public static SimulationConfig NavalBlockProcess()
        {
            var c = New(1046, 2000);
            var hull = SampleHull();
            var release = new SimuLean.Naval.BlockBreakdown.ReleaseOptions { BlockInterval = 12 };
            ElementConfig TableSource(string id, SimuLean.Naval.BlockBreakdown.Part part)
            {
                var e = El(id, "UnitySource");
                e.Parameters["sourceMode"] = "Schedule";
                e.Parameters["arrivalsTable"] = hull.Generate(part, release);
                return e;
            }
            ElementConfig Station(string id, int capacity, string expression)
            {
                var e = El(id, "UnityMultiServer");
                e.Parameters["capacity"] = capacity;
                e.Parameters["delayStrategyType"] = "Expression";
                e.Parameters["expression"] = expression;
                return e;
            }
            Add(c, TableSource("kits", SimuLean.Naval.BlockBreakdown.Part.SubBlocks));
            Add(c, TableSource("orders", SimuLean.Naval.BlockBreakdown.Part.Blocks));
            var sba = Station("subassembly", 2, "has(t_sba) ? t_sba : 6");
            sba.Parameters["outputStrategyType"] = "Expression";
            sba.Parameters["outputStrategy_expression"] = "item.turn == 1 ? 0 : 1";
            Add(c, sba);
            Add(c, Station("turning", 1, "2"));
            var yard = Queue("subblock_yard", 8);
            yard.Parameters[ElementBuildHelpers.ReleaseModeKey] = "AnyAccepted";
            Add(c, yard);
            var bays = El("block_bays", "UnityCombiner");
            bays.Parameters["requirements"] = new[] { 1 };
            bays.Parameters["capacity"] = 2;
            bays.Parameters["pullModeType"] = "SingleLabel";
            bays.Parameters["singleLabelName"] = "block";
            bays.Parameters["singleLabelValue"] = "";
            bays.Parameters["updateRequirements"] = true;
            bays.Parameters["updateLabels"] = new List<string> { "nsub" };
            bays.Parameters["delayStrategyType"] = "Expression";
            bays.Parameters["expression"] = "has(t_ba) ? t_ba : 16";
            bays.Parameters["inputOrder"] = new List<string> { "orders", "subblock_yard" };
            Add(c, bays);
            Add(c, Station("outfitting", 2, "8"));
            Add(c, Station("painting", 1, "10"));
            var blockYard = Queue("block_yard", 6);
            blockYard.Parameters[ElementBuildHelpers.ReleaseModeKey] = "AnyAccepted";
            Add(c, blockYard);
            var slipway = El("slipway", "UnityErectionDock");
            slipway.Parameters["capacity"] = 1;
            slipway.Parameters["erectionRule"] = "Sequence";
            slipway.Parameters["expectedBlocks"] = hull.BlockCount;
            slipway.Parameters["delayStrategyType"] = "Expression";
            slipway.Parameters["expression"] = "4 + 0.02 * weight";
            Add(c, slipway);
            var launch = Sink("launch");
            launch.Parameters["keepItems"] = true;
            Add(c, launch);
            Connect(c, "kits", "subassembly");
            Connect(c, "subassembly", "turning");          // output 0: turned sub-blocks
            Connect(c, "subassembly", "subblock_yard");    // output 1
            Connect(c, "turning", "subblock_yard");
            Connect(c, "orders", "block_bays");
            Connect(c, "subblock_yard", "block_bays");
            Connect(c, "block_bays", "outfitting");
            Connect(c, "outfitting", "painting");
            Connect(c, "painting", "block_yard");
            Connect(c, "block_yard", "slipway");
            Connect(c, "slipway", "launch");
            Team(c, "fitters", "", "", ("Fitter1", new[] { "skill=fitter" }), ("Fitter2", new[] { "skill=fitter" }));
            Team(c, "welders", "", "", ("Welder1", new[] { "skill=welder" }), ("Welder2", new[] { "skill=welder" }),
                 ("Welder3", new[] { "skill=welder" }), ("Welder4", new[] { "skill=welder" }));
            Team(c, "painters", "", "", ("Painter1", new[] { "skill=painter" }));
            Team(c, "crane", "", "", ("SlipwayCrane", new string[0]));
            ProcessBy(c, "subassembly", "fitters", "EndOfProcess", "");
            ProcessBy(c, "block_bays", "welders", "EndOfProcess", "", Requirement(2, "", "welders"));
            ProcessBy(c, "painting", "painters", "EndOfProcess", "");
            c.Elements.Find(x => x.Id == "painting").Parameters[ResourceUseKeys.ProcessHoldFor] = "Constant~6";
            ProcessBy(c, "slipway", "crane", "EndOfProcess", "");
            c.Elements.Find(x => x.Id == "slipway").Parameters[ResourceUseKeys.ProcessHoldFor] = "Constant~1";
            return c;
        }

        private static KeyValuePair<string, SimulationConfig> Pair(string n, SimulationConfig c)
            => new KeyValuePair<string, SimulationConfig>(n, c);

        // ── models ───────────────────────────────────────────────────────────

        /// <summary>Source → Queue → Server → Sink: the baseline path and link statistics.</summary>
        public static SimulationConfig BasicLine()
        {
            var c = New(1001, 2000);
            Add(c, Source("src", 1.0));
            Add(c, Queue("q", 100));
            Add(c, Server("srv", 1, "Exponential", ("exponentialRate", 1.25)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
            return c;
        }

        /// <summary>Source → Queue → MultiServer(3) → Sink: parallel slots.</summary>
        public static SimulationConfig MultiServer3()
        {
            var c = New(1002, 2000);
            Add(c, Source("src", 2.0));
            Add(c, Queue("q", 100));
            Add(c, Server("ms", 3, "Triangular", ("triangularMin", 0.8), ("triangularMode", 1.2), ("triangularMax", 2.0)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "ms"); Connect(c, "ms", "sink");
            return c;
        }

        /// <summary>Source → 2 servers (round robin) → Sink: output-strategy routing.</summary>
        public static SimulationConfig RoutingRoundRobin()
        {
            var c = New(1003, 2000);
            var src = Source("src", 1.5);
            src.Parameters["outputStrategyType"] = "RoundRobin";
            Add(c, src);
            Add(c, Server("a", 1, "Exponential", ("exponentialRate", 1.0)));
            Add(c, Server("b", 1, "Exponential", ("exponentialRate", 1.0)));
            Add(c, Sink("sink"));
            Connect(c, "src", "a"); Connect(c, "src", "b"); Connect(c, "a", "sink"); Connect(c, "b", "sink");
            return c;
        }

        /// <summary>Two servers sharing one operator (a team of one, kept until the item leaves): WAITING_FOR_RESOURCE.</summary>
        public static SimulationConfig OperatorPoolModel()
        {
            var c = New(1004, 2000);
            var src = Source("src", 1.2);
            src.Parameters["outputStrategyType"] = "ShortestQueue";
            Add(c, src);
            Add(c, Queue("q", 100));
            Add(c, Server("a", 1, "Uniform", ("uniformMin", 0.3), ("uniformMax", 0.9)));
            Add(c, Server("b", 1, "Uniform", ("uniformMin", 0.3), ("uniformMax", 0.9)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "a"); Connect(c, "q", "b"); Connect(c, "a", "sink"); Connect(c, "b", "sink");
            Team(c, "ops", "", "", ("Ops_1", new string[0]));
            ProcessBy(c, "a", "ops", "ItemLeaves", "");
            ProcessBy(c, "b", "ops", "ItemLeaves", "");
            return c;
        }

        /// <summary>
        /// Reference model: three stations share a team of two operators; a slow station behind them makes them block, and an
        /// operator stays with a blocked station until its item leaves (the process task's "hold until the
        /// item leaves" option).
        /// </summary>
        public static SimulationConfig ResourceSharedTeam()
        {
            var c = New(1015, 2000);
            var src = Source("src", 0.9);
            src.Parameters["outputStrategyType"] = "ShortestQueue";
            Add(c, src);
            Add(c, Queue("q", 100));
            Add(c, Server("a", 1, "Uniform", ("uniformMin", 1.0), ("uniformMax", 2.0)));
            Add(c, Server("b", 1, "Uniform", ("uniformMin", 1.0), ("uniformMax", 2.0)));
            Add(c, Server("c", 1, "Uniform", ("uniformMin", 1.0), ("uniformMax", 2.0)));
            Add(c, Queue("buffer", 2));
            Add(c, Server("slow", 1, "Exponential", ("exponentialRate", 1.0 / 0.95)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "a"); Connect(c, "q", "b"); Connect(c, "q", "c");
            Connect(c, "a", "buffer"); Connect(c, "b", "buffer"); Connect(c, "c", "buffer");
            Connect(c, "buffer", "slow"); Connect(c, "slow", "sink");
            Team(c, "team", "", "", ("Team_1", new string[0]), ("Team_2", new string[0]));
            foreach (var s in new[] { "a", "b", "c" }) ProcessBy(c, s, "team", "ItemLeaves", "");
            return c;
        }

        /// <summary>
        /// Two lines (welding, painting) served by one crew: members carry skill labels, each station asks for
        /// its skill, and among the qualified members the least used one is taken (resource order).
        /// </summary>
        public static SimulationConfig ResourceSkillQuery()
        {
            var c = New(1016, 2000);
            Add(c, Source("srcW", 2.0)); Add(c, Queue("qW", 100));
            Add(c, Source("srcP", 2.5)); Add(c, Queue("qP", 100));
            Add(c, Server("weld", 2, "Uniform", ("uniformMin", 1.0), ("uniformMax", 3.0)));
            Add(c, Server("paint", 2, "Uniform", ("uniformMin", 1.0), ("uniformMax", 3.0)));
            Add(c, Sink("sink"));
            Connect(c, "srcW", "qW"); Connect(c, "qW", "weld"); Connect(c, "weld", "sink");
            Connect(c, "srcP", "qP"); Connect(c, "qP", "paint"); Connect(c, "paint", "sink");
            Team(c, "crew", "", "|utilization",
                 ("Welder", new[] { "weld=1" }), ("Painter", new[] { "paint=1" }), ("AllRounder", new[] { "weld=1", "paint=1" }));
            ProcessBy(c, "weld", "crew", "EndOfProcess", "", Requirement(1, "weld == 1;|", "welding"));
            ProcessBy(c, "paint", "crew", "EndOfProcess", "", Requirement(1, "paint == 1;|", "painting"));
            return c;
        }

        /// <summary>
        /// A crew both processes at a station and carries its items to the next station (load + travel + unload);
        /// the destination queue is small, so moves wait for room.
        /// </summary>
        public static SimulationConfig ResourceTransport()
        {
            var c = New(1017, 2000);
            Add(c, Source("src", 2.0));
            Add(c, Queue("q", 100));
            Add(c, Server("m1", 1, "Uniform", ("uniformMin", 0.5), ("uniformMax", 1.5)));
            Add(c, Queue("buf", 2));
            Add(c, Server("m2", 1, "Uniform", ("uniformMin", 1.0), ("uniformMax", 2.0)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "m1"); Connect(c, "m1", "buf"); Connect(c, "buf", "m2"); Connect(c, "m2", "sink");
            Team(c, "crew", "", "", ("Op1", new string[0]), ("Op2", new string[0]));
            ProcessBy(c, "m1", "crew", "EndOfProcess", "");
            var m1 = c.Elements.Find(e => e.Id == "m1");
            m1.Parameters[ResourceUseKeys.TransportTeam] = "crew";
            m1.Parameters[ResourceUseKeys.TransportLoad] = "Constant~0.2";
            m1.Parameters[ResourceUseKeys.TransportTravel] = "Uniform~1~2";
            m1.Parameters[ResourceUseKeys.TransportUnload] = "Constant~0.2";
            return c;
        }

        /// <summary>A job needing two operators (all-or-nothing) next to jobs needing one, from a team of three.</summary>
        public static SimulationConfig ResourceCountTwo()
        {
            var c = New(1018, 2000);
            Add(c, Source("srcBig", 4.0)); Add(c, Queue("qBig", 100));
            Add(c, Source("srcSmall", 1.5)); Add(c, Queue("qSmall", 100));
            Add(c, Server("big", 1, "Uniform", ("uniformMin", 1.0), ("uniformMax", 3.0)));
            Add(c, Server("small", 2, "Uniform", ("uniformMin", 0.5), ("uniformMax", 2.0)));
            Add(c, Sink("sink"));
            Connect(c, "srcBig", "qBig"); Connect(c, "qBig", "big"); Connect(c, "big", "sink");
            Connect(c, "srcSmall", "qSmall"); Connect(c, "qSmall", "small"); Connect(c, "small", "sink");
            Team(c, "team", "", "", ("Op1", new string[0]), ("Op2", new string[0]), ("Op3", new string[0]));
            ProcessBy(c, "big", "team", "EndOfProcess", "", Requirement(2, "", ""));
            ProcessBy(c, "small", "team", "EndOfProcess", "");
            return c;
        }

        /// <summary>Two stations share one operator; requests are served by the item's <c>prio</c> label.</summary>
        public static SimulationConfig ResourceRequestPriority()
        {
            var c = New(1019, 2000);
            var hi = Source("srcHigh", 3.0); hi.Parameters["assignLabels"] = new List<string> { "prio=5" };
            var lo = Source("srcLow", 2.0); lo.Parameters["assignLabels"] = new List<string> { "prio=1" };
            Add(c, hi); Add(c, lo);
            Add(c, Queue("qHigh", 1000)); Add(c, Queue("qLow", 1000));
            Add(c, Server("high", 1, "Constant", ("constantValue", 0.9)));
            Add(c, Server("low", 1, "Constant", ("constantValue", 0.9)));
            Add(c, Sink("sink"));
            Connect(c, "srcHigh", "qHigh"); Connect(c, "qHigh", "high"); Connect(c, "high", "sink");
            Connect(c, "srcLow", "qLow"); Connect(c, "qLow", "low"); Connect(c, "low", "sink");
            Team(c, "ops", "|priority desc", "", ("Op", new string[0]));
            ProcessBy(c, "high", "ops", "EndOfProcess", "prio");
            ProcessBy(c, "low", "ops", "EndOfProcess", "prio");
            return c;
        }

        private static void Team(SimulationConfig c, string id, string requestOrder, string resourceOrder,
                                 params (string name, string[] labels)[] members)
        {
            var t = new ResourceTeamConfig { Id = id, Name = id, RequestOrder = requestOrder, ResourceOrder = resourceOrder };
            foreach (var m in members) t.Members.Add(new ResourceMemberConfig { Name = m.name, Labels = new List<string>(m.labels) });
            c.ResourceTeams.Add(t);
        }

        private static string Requirement(int count, string query, string label) => ResourceBuild.EncodeRequirement(count, query, label);

        private static void ProcessBy(SimulationConfig c, string element, string team, string release, string priority,
                                      params string[] requirements)
        {
            var e = c.Elements.Find(x => x.Id == element);
            e.Parameters[ResourceUseKeys.ProcessTeam] = team;
            e.Parameters[ResourceUseKeys.ProcessRelease] = release;
            if (!string.IsNullOrEmpty(priority)) e.Parameters[ResourceUseKeys.ProcessPriority] = priority;
            if (requirements.Length > 0) e.Parameters[ResourceUseKeys.ProcessRequirements] = new List<string>(requirements);
        }

        /// <summary>Main + two component sources → Combiner → Sink: connection-order input mapping.</summary>
        public static SimulationConfig CombinerModel()
        {
            var c = New(1005, 2000);
            Add(c, Source("main", 4.0));
            Add(c, Source("compA", 1.5));
            Add(c, Source("compB", 3.0));
            var comb = El("comb", "UnityCombiner");
            comb.Parameters["requirements"] = new[] { 2, 1 };
            comb.Parameters["batchMode"] = true;
            comb.Parameters["delayStrategyType"] = "Constant";
            comb.Parameters["constantValue"] = 2.0;
            comb.Parameters["inputOrder"] = new List<string> { "main", "compA", "compB" };
            Add(c, comb);
            Add(c, Sink("sink"));
            Connect(c, "main", "comb"); Connect(c, "compA", "comb"); Connect(c, "compB", "comb"); Connect(c, "comb", "sink");
            return c;
        }

        /// <summary>Containers pick 2 units from a stock port; surplus returns to a sink.</summary>
        public static SimulationConfig PickingModel()
        {
            var c = New(1006, 2000);
            Add(c, ScheduleSource("orders", 200, 10.0, ("pick_Item", "2")));
            Add(c, Source("stock", 3.0));
            var ps = El("pick", "UnityPickingStation");
            ps.Parameters["stockCapacity"] = 20;
            ps.Parameters["useContainerLabels"] = true;
            ps.Parameters["labelPrefix"] = "pick_";
            ps.Parameters["delayStrategyType"] = "Constant";
            ps.Parameters["constantValue"] = 1.5;
            Add(c, ps);
            Add(c, Sink("out"));
            Add(c, Sink("returns"));
            Connect(c, "orders", "pick"); Connect(c, "pick", "out");
            c.Connections.Add(new ConnectionConfig("stock", "pick", "StockInput"));
            c.Connections.Add(new ConnectionConfig("pick", "returns", "ReturnsOutput"));
            return c;
        }

        /// <summary>Jobs run a two-task precedence network; task b consumes one stock item.</summary>
        public static SimulationConfig AssemblyModel()
        {
            var c = New(1007, 2000);
            Add(c, Source("jobs", 8.0));
            Add(c, Source("stock", 5.0));
            var asy = El("asy", "UnityAssemblyStation");
            asy.Parameters["workerCount"] = 2;
            asy.Parameters["stockCapacity"] = 10;
            asy.Parameters["assemblyTasks"] = new List<string>
            {
                "a|||0|Exponential~0.25",
                "b|a|Item:1|0|Constant~2",
                "c|||1|Uniform~1~3",
            };
            Add(c, asy);
            Add(c, Sink("sink"));
            Connect(c, "jobs", "asy"); Connect(c, "asy", "sink");
            c.Connections.Add(new ConnectionConfig("stock", "asy", "StockInput"));
            return c;
        }

        /// <summary>Stacker with 2 positions of 4: compound release and stacking statistics.</summary>
        public static SimulationConfig StackerModel()
        {
            var c = New(1008, 2000);
            Add(c, Source("src", 1.0));
            var st = El("st", "UnityStacker");
            StackerBuilder.Encode(st, new StackerBuilder.Settings
            {
                Positions = 2, Capacity = 4, SelectionRule = StackSelectionRule.RoundRobin, CycleTime = "Constant~0.5",
            });
            Add(c, st);
            Add(c, Sink("sink"));
            Connect(c, "src", "st"); Connect(c, "st", "sink");
            return c;
        }

        /// <summary>
        /// Source → queue at node n1 ⇒ an AGV (team on the network, 10 m at 1 m/s, load / unload 1) ⇒ queue at
        /// node n2 → Sink: vehicle transport through the dispatcher (travel empty, load, travel, unload).
        /// </summary>
        public static SimulationConfig TransportModel()
        {
            var c = New(1009, 2000);
            c.Network = Network(("n1", 0), ("n2", 10));
            Add(c, Source("src", 25.0));
            Add(c, Placed(Queue("q1", 5), "n1"));
            Add(c, Placed(Queue("q2", 5), "n2"));
            Add(c, Sink("sink"));
            Connect(c, "src", "q1"); Connect(c, "q1", "q2"); Connect(c, "q2", "sink");
            Fleet(c, "agv", "n1", 1.0, "", 1);
            TransportBy(c, "q1", "agv");
            return c;
        }

        /// <summary>
        /// Two origins at the ends of a line (A — D — B, 25 m each) send to a dock D; a fleet of two AGVs takes
        /// the <b>nearest</b> free vehicle (resource order <c>distance</c>) — vehicles wait for work where they
        /// unloaded.
        /// </summary>
        public static SimulationConfig ResourceFleetNearest()
        {
            var c = New(1020, 3000);
            c.Network = Network(("A", 0), ("D", 25), ("B", 50));
            Add(c, Source("srcA", 60.0)); Add(c, Source("srcB", 90.0));
            Add(c, Placed(Queue("qA", 100), "A")); Add(c, Placed(Queue("qB", 100), "B"));
            Add(c, Placed(Queue("dock", 100), "D"));
            Add(c, Sink("sink"));
            Connect(c, "srcA", "qA"); Connect(c, "srcB", "qB");
            Connect(c, "qA", "dock"); Connect(c, "qB", "dock"); Connect(c, "dock", "sink");
            Fleet(c, "fleet", "A", 1.0, "|distance", 2);
            TransportBy(c, "qA", "fleet");
            TransportBy(c, "qB", "fleet");
            return c;
        }

        /// <summary>
        /// Two operators walking on a distance matrix: they walk to station m1 or m2 to process (resource order
        /// <c>distance</c> = the nearest), and carry m1's items to m2.
        /// </summary>
        public static SimulationConfig ResourceWalkMatrix()
        {
            var c = New(1021, 3000);
            var mx = new TravelMatrixConfig { Id = "floor" };
            mx.Entries.Add(ResourceBuild.MatrixEntry("H", "S1", 5));
            mx.Entries.Add(ResourceBuild.MatrixEntry("H", "S2", 8));
            mx.Entries.Add(ResourceBuild.MatrixEntry("S1", "S2", 6));
            c.TravelMatrices.Add(mx);
            Add(c, Source("src", 12.0));
            Add(c, Queue("q", 100));
            Add(c, Placed(Server("m1", 1, "Uniform", ("uniformMin", 2.0), ("uniformMax", 4.0)), "S1"));
            Add(c, Placed(Queue("buf", 3), "S2"));
            Add(c, Placed(Server("m2", 1, "Uniform", ("uniformMin", 2.0), ("uniformMax", 5.0)), "S2"));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "m1"); Connect(c, "m1", "buf"); Connect(c, "buf", "m2"); Connect(c, "m2", "sink");
            var t = new ResourceTeamConfig { Id = "ops", Name = "ops", ResourceOrder = "|distance", Travel = "Matrix", Matrix = "floor", Speed = 1.2, HomeNode = "H" };
            t.Members.Add(new ResourceMemberConfig { Name = "Op1", LoadTime = 0.5, UnloadTime = 0.5 });
            t.Members.Add(new ResourceMemberConfig { Name = "Op2", LoadTime = 0.5, UnloadTime = 0.5 });
            c.ResourceTeams.Add(t);
            ProcessBy(c, "m1", "ops", "EndOfProcess", "");
            ProcessBy(c, "m2", "ops", "EndOfProcess", "");
            TransportBy(c, "m1", "ops");
            return c;
        }

        /// <summary>
        /// Patients that walk by themselves (WalkLink): they enter at the door, walk 15 m to a waiting area of
        /// three, 10 m to the consultation and 15 m to the exit at 1.2 m/s (time unit 1 s), between the network's nodes.
        /// </summary>
        public static SimulationConfig WalkingPatients()
        {
            var c = New(1031, 20000);
            c.Network = Network(("Door", 0), ("Wait", 15), ("Room", 25), ("Exit", 40));
            Add(c, Walk(Placed(Source("src", 60.0), "Door")));
            Add(c, Walk(Placed(Queue("wait", 3), "Wait")));
            Add(c, Walk(Placed(Server("room", 1, "Uniform", ("uniformMin", 30.0), ("uniformMax", 70.0)), "Room")));
            Add(c, Placed(Sink("exit"), "Exit"));
            Connect(c, "src", "wait"); Connect(c, "wait", "room"); Connect(c, "room", "exit");
            return c;
        }

        private static ElementConfig Walk(ElementConfig e)
        {
            e.Parameters[ResourceUseKeys.WalkSpeed] = 1.2;
            return e;
        }

        /// <summary>A robot on axes (with acceleration) carries between two machines of a cell.</summary>
        public static SimulationConfig ResourceRobotCell()
        {
            var c = New(1022, 2000);
            c.Network = Points(("A", 0, 0, 0), ("B", 1.5, 0.8, 0.4), ("Park", 0.7, 1.2, 0));
            Add(c, Source("src", 9.0));
            Add(c, Queue("q", 100));
            Add(c, Placed(Server("m1", 1, "Uniform", ("uniformMin", 3.0), ("uniformMax", 5.0)), "A"));
            Add(c, Placed(Queue("in2", 1), "B"));
            Add(c, Placed(Server("m2", 1, "Uniform", ("uniformMin", 3.0), ("uniformMax", 5.5)), "B"));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "m1"); Connect(c, "m1", "in2"); Connect(c, "in2", "m2"); Connect(c, "m2", "sink");
            var t = new ResourceTeamConfig { Id = "robot", Name = "robot", Travel = "Axes", HomeNode = "Park",
                                             AxisSpeed = ResourceBuild.TripleSpec(1, 1, 0.5), AxisAccel = ResourceBuild.TripleSpec(2, 2, 1) };
            t.Members.Add(new ResourceMemberConfig { Name = "Robot", LoadTime = 0.4, UnloadTime = 0.4 });
            c.ResourceTeams.Add(t);
            TransportBy(c, "m1", "robot");
            return c;
        }

        /// <summary>A stacker whose cycles a gantry crane does (axes x / y / z): pick point K, two stacks.</summary>
        public static SimulationConfig ResourceGantryStacker()
        {
            var c = New(1023, 2000);
            c.Network = Points(("K", 0, 0, 1), ("P0", 3, 0, 0), ("P1", 3, 2, 0));
            Add(c, Source("src", 9.0));
            Add(c, Queue("q", 100));
            var st = El("st", "UnityStacker");
            StackerBuilder.Encode(st, new StackerBuilder.Settings
            {
                Positions = 2, Capacity = 5, SelectionRule = StackSelectionRule.RoundRobin,
                PickLocation = "K", PositionLocations = new List<string> { "P0", "P1" },
            });
            Add(c, st);
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "st"); Connect(c, "st", "sink");
            var t = new ResourceTeamConfig { Id = "gantry", Name = "gantry", Travel = "Axes", HomeNode = "K",
                                             AxisSpeed = ResourceBuild.TripleSpec(1.5, 1, 0.5), AxisAccel = ResourceBuild.TripleSpec(1, 1, 1) };
            t.Members.Add(new ResourceMemberConfig { Name = "Gantry", LoadTime = 0.5, UnloadTime = 0.5 });
            c.ResourceTeams.Add(t);
            ProcessBy(c, "st", "gantry", "EndOfProcess", "");
            return c;
        }

        /// <summary>
        /// Operators walking freely on a walk grid (A*) around a wall between two stations: they process at both
        /// and carry from the first to the second (nearest operator).
        /// </summary>
        public static SimulationConfig ResourceWalkGrid()
        {
            var c = New(1024, 3000);
            c.Network = Points(("H", 1.25, 0, 7.25), ("S1", 1.25, 0, 1.25), ("S2", 8.75, 0, 1.25));
            c.WalkGrids.Add(new WalkGridConfig
            {
                Id = "floor", CellSize = 0.5,
                Rows = new List<string>
                {
                    "..........#.........", "..........#.........", "..........#.........", "..........#.........",
                    "..........#.........", "..........#.........", "..........#.........", "..........#.........",
                    "..........#.........", "..........#.........", "..........#.........", "....................",
                    "....................", "....................", "....................", "....................",
                },
            });
            Add(c, Source("src", 20.0));
            Add(c, Queue("q", 100));
            Add(c, Placed(Server("m1", 1, "Uniform", ("uniformMin", 2.0), ("uniformMax", 4.0)), "S1"));
            Add(c, Placed(Queue("buf", 3), "S2"));
            Add(c, Placed(Server("m2", 1, "Uniform", ("uniformMin", 2.0), ("uniformMax", 5.0)), "S2"));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "m1"); Connect(c, "m1", "buf"); Connect(c, "buf", "m2"); Connect(c, "m2", "sink");
            var t = new ResourceTeamConfig { Id = "ops", Name = "ops", ResourceOrder = "|distance", Travel = "Grid", Grid = "floor", Speed = 1.2, HomeNode = "H" };
            t.Members.Add(new ResourceMemberConfig { Name = "Op1", LoadTime = 0.5, UnloadTime = 0.5 });
            t.Members.Add(new ResourceMemberConfig { Name = "Op2", LoadTime = 0.5, UnloadTime = 0.5 });
            c.ResourceTeams.Add(t);
            ProcessBy(c, "m1", "ops", "EndOfProcess", "");
            ProcessBy(c, "m2", "ops", "EndOfProcess", "");
            TransportBy(c, "m1", "ops");
            return c;
        }

        /// <summary>
        /// <c>resource_walk_matrix</c> with travel times calibrated (as if measured with Unity physics): the matrix is
        /// the estimate, some pairs have their own observed means, the rest the general model.
        /// </summary>
        public static SimulationConfig ResourceWalkCalibrated()
        {
            var c = ResourceWalkMatrix();
            c.Seed = 1025;
            var t = c.ResourceTeams[0];
            t.Travel = "Calibrated"; t.CalibrationBase = "Matrix"; t.CalibrationFit = "0.8|0.85|0.4";
            t.CalibrationPairs.Add("S1|S2|1|6.9|0.6|12");
            t.CalibrationPairs.Add("S2|S1|0|6.1|0.5|11");
            t.CalibrationPairs.Add("H|S1|0|5.2|0.4|2");     // too few samples: general model
            return c;
        }

        /// <summary><c>resource_fleet_nearest</c> with three AGVs under traffic control on its single-lane line: they
        /// queue behind each other, meet head-on (deadlocks resolved) and wait at nodes.</summary>
        public static SimulationConfig ResourceAgvTraffic()
        {
            var c = ResourceFleetNearest();
            c.Seed = 1026;
            var t = c.ResourceTeams[0];
            t.Traffic = true;
            t.Members.Add(new ResourceMemberConfig { Name = t.Id + "_3", LoadTime = 1, UnloadTime = 1 });
            return c;
        }

        // Points (nodes without tracks): where kinematic resources travel between.
        private static NetworkConfig Points(params (string id, double x, double y, double z)[] pts)
        {
            var n = new NetworkConfig();
            foreach (var p in pts) n.Nodes.Add(TransportStorageConfigCodec.FormatNode(p.id, p.x, p.y, p.z));
            return n;
        }

        // A straight network: nodes at x along a line, consecutive nodes joined by two-way travel tracks.
        private static NetworkConfig Network(params (string id, double x)[] nodes)
        {
            var n = new NetworkConfig();
            for (int i = 0; i < nodes.Length; i++)
            {
                n.Nodes.Add(TransportStorageConfigCodec.FormatNode(nodes[i].id, nodes[i].x, 0, 0));
                if (i > 0)
                    n.Tracks.Add(TransportStorageConfigCodec.FormatTrack(nodes[i - 1].id, nodes[i].id,
                        Math.Abs(nodes[i].x - nodes[i - 1].x), true, Transport.TrackType.TravelPath));
            }
            return n;
        }

        private static ElementConfig Placed(ElementConfig e, string node)
        {
            e.Parameters["location"] = node;
            return e;
        }

        // A team of vehicles on the network (load / unload 1 each).
        private static void Fleet(SimulationConfig c, string id, string home, double speed, string resourceOrder, int vehicles)
        {
            var t = new ResourceTeamConfig { Id = id, Name = id, ResourceOrder = resourceOrder, Travel = "Network", Speed = speed, HomeNode = home };
            for (int i = 1; i <= vehicles; i++) t.Members.Add(new ResourceMemberConfig { Name = id + "_" + i, LoadTime = 1, UnloadTime = 1 });
            c.ResourceTeams.Add(t);
        }

        private static void TransportBy(SimulationConfig c, string element, string team)
            => c.Elements.Find(x => x.Id == element).Parameters[ResourceUseKeys.TransportTeam] = team;

        /// <summary>Basic line whose server fails (MTBF/MTTR): pausable work and downtime accounting.</summary>
        public static SimulationConfig DowntimeModel()
        {
            var c = BasicLine();
            c.Seed = 1010;
            var dt = new DowntimeConfig("fail", "MtbfMttr", "srv");
            dt.Parameters["ttf"] = "ExponentialMean~60";
            dt.Parameters["ttr"] = "ExponentialMean~6";
            c.Downtimes.Add(dt);
            return c;
        }

        /// <summary>
        /// Source → a two-lane accumulation buffer limited by length (lanes 6 and 4, gap 0.5, items 1 or 2.5 long by
        /// their <c>length</c> label) → Server → Sink (R-11).
        /// </summary>
        public static SimulationConfig LengthLimitedLanes()
        {
            var c = New(1013, 2000);
            var src = Source("src", 1.0);
            src.Parameters["assignLabels"] = new List<string> { "length=1" };
            Add(c, src);
            var src2 = Source("src2", 4.0);
            src2.Parameters["assignLabels"] = new List<string> { "length=2.5" };
            Add(c, src2);
            var lanes = El("lanes", "UnityLengthLimitedQueue");
            SimuLean.Serialization.LengthLimitedQueueBuilder.Encode(lanes, new[] { 6.0, 4.0 }, 0.5, 1.0, "length", 0, true);
            Add(c, lanes);
            Add(c, Server("srv", 1, "Exponential", ("exponentialRate", 1.6)));
            Add(c, Sink("sink"));
            Connect(c, "src", "lanes"); Connect(c, "src2", "lanes"); Connect(c, "lanes", "srv"); Connect(c, "srv", "sink");
            return c;
        }

        /// <summary>
        /// One week in minutes from Monday 2026-10-05 00:00; the server works Mon–Fri 06:00–14:00 (a shift downtime,
        /// 40 h of 168 h), arrivals all week (R-11).
        /// </summary>
        public static SimulationConfig ShiftModel()
        {
            var c = New(1014, 7 * 24 * 60);
            c.StartDateTime = "2026-10-05 00:00";
            c.TimeUnitSeconds = 60;
            Add(c, Source("src", 5.0));
            Add(c, Queue("q", 5000));
            Add(c, Server("srv", 1, "Exponential", ("exponentialRate", 0.5)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
            var shift = new DowntimeConfig("shift", "Shift", "srv");
            shift.Parameters["pattern"] = "Mon-Fri 06:00-14:00";
            c.Downtimes.Add(shift);
            return c;
        }

        /// <summary>Multi-server model with a warm-up: statistics cover [500, 2500] only.</summary>
        public static SimulationConfig WarmupModel()
        {
            var c = MultiServer3();
            c.Seed = 1011;
            c.MaxSimulationTime = 2500;
            c.WarmupTime = 500;
            return c;
        }

        /// <summary>
        /// Two queues push their items (labels <c>prio</c> 1 or 5) to the list <c>work</c>; two servers pull
        /// the highest priority first (list default order): a model-wide priority queue.
        /// </summary>
        public static SimulationConfig ListPriorityDispatch()
        {
            var c = New(1012, 3000);
            Add(c, ScheduleSourceBy("lowSrc", 500, 6.0, 0.0, i => ("prio", "1")));
            Add(c, ScheduleSourceBy("highSrc", 500, 6.0, 3.0, i => ("prio", "5")));
            Add(c, Queue("lowQ", 1000));
            Add(c, Queue("highQ", 1000));
            Add(c, Server("s1", 1, "Exponential", ("exponentialRate", 1.0 / 5.5)));
            Add(c, Server("s2", 1, "Exponential", ("exponentialRate", 1.0 / 5.5)));
            Add(c, Sink("sink"));
            Connect(c, "lowSrc", "lowQ"); Connect(c, "highSrc", "highQ");
            Connect(c, "s1", "sink"); Connect(c, "s2", "sink");
            c.Lists.Add(new ListConfig { Id = "work", Name = "work", DefaultOrderBy = "prio", DefaultDescending = true });
            Push(c, "lowQ", "work"); Push(c, "highQ", "work");
            Pull(c, "work", "s1", ""); Pull(c, "work", "s2", "");
            return c;
        }

        /// <summary>One queue pushes items of kind 1 / 2 to a list; each server pulls only its kind.</summary>
        public static SimulationConfig ListFilteredPull()
        {
            var c = New(1013, 2000);
            Add(c, ScheduleSourceBy("src", 600, 3.0, 0.0, i => ("kind", i % 3 == 0 ? "2" : "1")));
            Add(c, Queue("q", 1000));
            Add(c, Server("kind1", 1, "Uniform", ("uniformMin", 2.0), ("uniformMax", 4.0)));
            Add(c, Server("kind2", 1, "Uniform", ("uniformMin", 4.0), ("uniformMax", 8.0)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "kind1", "sink"); Connect(c, "kind2", "sink");
            c.Lists.Add(new ListConfig { Id = "byKind", Name = "byKind" });
            Push(c, "q", "byKind");
            Pull(c, "byKind", "kind1", "kind == 1|age desc");
            Pull(c, "byKind", "kind2", "kind == 2|age desc");
            return c;
        }

        /// <summary>
        /// Combiner whose main input is a connection and whose component input pulls from a list fed by
        /// two queues (source labels <c>prio</c> 1 / 5, highest first): <c>inputOrder</c> placeholder + port.
        /// </summary>
        public static SimulationConfig ListCombiner()
        {
            var c = New(1014, 2000);
            Add(c, Source("main", 4.0));
            var low = Source("lowParts", 4.0);  low.Parameters["assignLabels"]  = new List<string> { "prio=1" };
            var high = Source("highParts", 6.0); high.Parameters["assignLabels"] = new List<string> { "prio=5" };
            Add(c, low); Add(c, high);
            Add(c, Queue("lowQ", 1000));
            Add(c, Queue("highQ", 1000));
            var comb = El("comb", "UnityCombiner");
            comb.Parameters["requirements"] = new[] { 2 };
            comb.Parameters["batchMode"] = true;
            comb.Parameters["delayStrategyType"] = "Constant";
            comb.Parameters["constantValue"] = 1.5;
            comb.Parameters["inputOrder"] = new List<string> { "main", "" };
            Add(c, comb);
            Add(c, Sink("sink"));
            Connect(c, "main", "comb"); Connect(c, "lowParts", "lowQ"); Connect(c, "highParts", "highQ"); Connect(c, "comb", "sink");
            c.Lists.Add(new ListConfig { Id = "parts", Name = "parts", DefaultOrderBy = "prio", DefaultDescending = true });
            Push(c, "lowQ", "parts"); Push(c, "highQ", "parts");
            Pull(c, "parts", "comb", "", port: 1);
            return c;
        }

        private static void Push(SimulationConfig c, string element, string list)
            => c.Connections.Add(new ConnectionConfig(element, list, "PushToList"));

        private static void Pull(SimulationConfig c, string list, string element, string query, int port = -1)
        {
            var conn = new ConnectionConfig(list, element, "PullFromList");
            conn.Parameters["query"] = query;
            if (port >= 0) conn.Parameters["port"] = port;
            c.Connections.Add(conn);
        }

        /// <summary>Schedule source: <paramref name="rows"/> arrivals every <paramref name="gap"/> from
        /// <paramref name="offset"/>, with one label whose value depends on the row.</summary>
        private static ElementConfig ScheduleSourceBy(string id, int rows, double gap, double offset, Func<int, (string col, string value)> label)
        {
            string col = label(0).col;
            var table = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string>(), ["Name"] = new List<string>(), ["Q"] = new List<string>(), [col] = new List<string>(),
            };
            for (int i = 0; i < rows; i++)
            {
                table["Time"].Add((offset + i * gap).ToString(System.Globalization.CultureInfo.InvariantCulture));
                table["Name"].Add(id);
                table["Q"].Add("1");
                table[col].Add(label(i).value);
            }
            var e = El(id, "UnitySource");
            e.Parameters["sourceMode"] = "Schedule";
            e.Parameters["arrivalsTable"] = table;
            return e;
        }

        // ── run control ───────────────────────────────────────────

        /// <summary>The basic line with an end condition: stop when the sink has received 500 items (end time = makespan).</summary>
        public static SimulationConfig EndAtSinkCount()
        {
            var c = BasicLine();
            c.StopAt.Add("sink");
            c.StopAtCount = 500;
            return c;
        }

        /// <summary>300 scheduled items through a server; the run ends when the model is empty, long before its horizon.</summary>
        public static SimulationConfig EndWhenEmpty()
        {
            var c = New(1031, 5000);
            Add(c, ScheduleSource("src", 300, 1.0, ("kind", "a")));
            Add(c, Queue("q", 1000));
            Add(c, Server("srv", 1, "Exponential", ("exponentialRate", 1.25)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
            c.StopWhenEmpty = true;
            return c;
        }

        /// <summary>A Combiner needs 2 components per main item: 3 mains and 4 components, so the third main waits forever —
        /// the run stalls (nothing blocked: a starved kit); flagged, and the run goes on to its horizon.</summary>
        public static SimulationConfig StallCombiner()
        {
            var c = New(1032, 1000);
            Add(c, ScheduleSource("main", 3, 10.0, ("kind", "m")));
            Add(c, ScheduleSource("comp", 4, 1.0, ("kind", "c")));
            var comb = El("comb", "UnityCombiner");
            comb.Parameters["requirements"] = new[] { 2 };
            comb.Parameters["delayStrategyType"] = "Constant";
            comb.Parameters["constantValue"] = 1.0;
            comb.Parameters["inputOrder"] = new List<string> { "main", "comp" };
            Add(c, comb);
            Add(c, Sink("sink"));
            Connect(c, "main", "comb"); Connect(c, "comp", "comb"); Connect(c, "comb", "sink");
            return c;
        }

        /// <summary>6 scheduled items through a server into a buffer of 2 that nothing takes from: the server ends BLOCKED
        /// and the source holds the rest — a deadlock (flagged with what each element waits for).</summary>
        public static SimulationConfig DeadlockDeadEnd()
        {
            var c = New(1033, 1000);
            Add(c, ScheduleSource("src", 6, 1.0, ("kind", "a")));
            Add(c, Server("srv", 1, "Constant", ("constantValue", 0.5)));
            Add(c, Queue("buffer", 2));
            Connect(c, "src", "srv"); Connect(c, "srv", "buffer");
            return c;
        }

        /// <summary>
        /// 4 scheduled blocks assembled with a kit each from an unlimited kit source through a rack of 3,
        /// into the "hull": two queues of 2 without outputs (no sink). When the 4th block is in, the kit source is blocked
        /// behind the full rack and nothing will take the kits: the run is <b>finished</b> (surplus, not a deadlock), ends
        /// there ("when empty"), and its makespan is the last entry into the hull.
        /// </summary>
        public static SimulationConfig FinishWithSurplus()
        {
            var c = New(1037, 1000);
            Add(c, ScheduleSource("blocks", 4, 10.0, ("kind", "block")));
            Add(c, Source("kits", 2.0));
            Add(c, Queue("rack", 3));
            var asm = El("assembly", "UnityCombiner");
            asm.Parameters["requirements"] = new[] { 1 };
            asm.Parameters["delayStrategyType"] = "Constant";
            asm.Parameters["constantValue"] = 4.0;
            asm.Parameters["inputOrder"] = new List<string> { "blocks", "rack" };
            Add(c, asm);
            Add(c, Queue("hull1", 2));
            Add(c, Queue("hull2", 2));
            Connect(c, "blocks", "assembly"); Connect(c, "kits", "rack"); Connect(c, "rack", "assembly");
            Connect(c, "assembly", "hull1"); Connect(c, "assembly", "hull2");
            c.StopWhenEmpty = true;
            return c;
        }

        /// <summary>The same yard ended by the hull instead (#27): stop when the two hull queues have received the 4 blocks.</summary>
        public static SimulationConfig EndAtStorage()
        {
            var c = FinishWithSurplus();
            c.StopWhenEmpty = false;
            c.StopAt.Add("hull1");
            c.StopAt.Add("hull2");
            c.StopAtCount = 4;
            return c;
        }

        /// <summary>
        /// a welding cell (Combiner, 1 kit per block) and a prep station share one welder, who walks between
        /// them. While the cell waits for the welder it already takes the kit of its next block (the components of the
        /// next job). 2 blocks and 4 kits (through a rack of 5): when the 2nd block is out, the cell holds the
        /// kit it took for a 3rd block that will never come (the block source is exhausted) and the rack another. No
        /// material: the run is <b>finished</b> (starved), not a deadlock, and the cell is reported waiting for its main
        /// input.
        /// </summary>
        public static SimulationConfig CombinerStarved()
        {
            var c = New(1038, 1000);
            var mx = new TravelMatrixConfig { Id = "floor" };
            mx.Entries.Add(ResourceBuild.MatrixEntry("H", "P", 2));
            mx.Entries.Add(ResourceBuild.MatrixEntry("H", "C", 3));
            mx.Entries.Add(ResourceBuild.MatrixEntry("P", "C", 5));
            c.TravelMatrices.Add(mx);
            Add(c, ScheduleSource("blocks", 2, 1.0, ("kind", "block")));
            Add(c, Placed(Server("prep", 1, "Constant", ("constantValue", 2.0)), "P"));
            Add(c, ScheduleSource("kits", 4, 1.0, ("kind", "kit")));
            Add(c, Queue("rack", 5));
            var asm = El("assembly", "UnityCombiner");
            asm.Parameters["requirements"] = new[] { 1 };
            asm.Parameters["delayStrategyType"] = "Constant";
            asm.Parameters["constantValue"] = 4.0;
            asm.Parameters["inputOrder"] = new List<string> { "prep", "rack" };
            Add(c, Placed(asm, "C"));
            Add(c, Sink("erected"));
            Connect(c, "blocks", "prep"); Connect(c, "prep", "assembly"); Connect(c, "kits", "rack"); Connect(c, "rack", "assembly");
            Connect(c, "assembly", "erected");
            var t = new ResourceTeamConfig { Id = "welders", Name = "welders", Travel = "Matrix", Matrix = "floor", Speed = 1.0, HomeNode = "H" };
            t.Members.Add(new ResourceMemberConfig { Name = "Welder1" });
            c.ResourceTeams.Add(t);
            ProcessBy(c, "prep", "welders", "EndOfProcess", "");
            ProcessBy(c, "assembly", "welders", "EndOfProcess", "");
            return c;
        }

        // ── matching + transport ───────────

        /// <summary>
        /// A yard (buffer of 4) serves two assembly bays by key: each bay (a Combiner, pull mode SingleLabel on
        /// <c>block</c> with no value = its main item's block) takes the sub-block of its own block; a crane (team
        /// <c>crane</c>, the yard's transport) carries each sub-block. Sub-blocks arrive 1, 2, 3, 4, but bay A builds
        /// block 3 first, then 1 (out of order). <paramref name="release"/> AnyAccepted: the yard offers every held
        /// sub-block (FIFO would deadlock: block 1 at the head, wanted by nobody yet).
        /// </summary>
        public static SimulationConfig YardKitting(string release = "AnyAccepted")
        {
            var c = New(1041, 200);
            Add(c, ScheduleSourceBy("blocksA", 2, 0.0, 0.0, i => ("block", i == 0 ? "3" : "1")));
            Add(c, ScheduleSourceBy("blocksB", 2, 0.0, 0.0, i => ("block", i == 0 ? "2" : "4")));
            Add(c, ScheduleSourceBy("subs", 4, 1.0, 0.0, i => ("block", (i + 1).ToString())));
            var yard = Queue("yard", 4);
            yard.Parameters["releaseMode"] = release;
            Add(c, yard);
            Add(c, Bay("bayA", "blocksA", 1));
            Add(c, Bay("bayB", "blocksB", 1));
            Add(c, Sink("erectedA")); Add(c, Sink("erectedB"));
            Connect(c, "subs", "yard");
            Connect(c, "blocksA", "bayA"); Connect(c, "yard", "bayA");
            Connect(c, "blocksB", "bayB"); Connect(c, "yard", "bayB");
            Connect(c, "bayA", "erectedA"); Connect(c, "bayB", "erectedB");
            Crane(c, "yard");
            return c;
        }

        /// <summary>The yard as a list (kitting by key through a list): the bays pull "the sub-block of my block"; the
        /// crane is the yard's transport (the holder's, as FlexSim's <i>Use Transport</i>).</summary>
        public static SimulationConfig YardKittingList()
        {
            var c = New(1042, 200);
            Add(c, ScheduleSourceBy("blocksA", 2, 0.0, 0.0, i => ("block", i == 0 ? "3" : "1")));
            Add(c, ScheduleSourceBy("blocksB", 2, 0.0, 0.0, i => ("block", i == 0 ? "2" : "4")));
            Add(c, ScheduleSourceBy("subs", 4, 1.0, 0.0, i => ("block", (i + 1).ToString())));
            Add(c, Queue("yard", 4));
            var a = Bay("bayA", "blocksA", 1); a.Parameters["inputOrder"] = new List<string> { "blocksA", "" };
            var b = Bay("bayB", "blocksB", 1); b.Parameters["inputOrder"] = new List<string> { "blocksB", "" };
            Add(c, a); Add(c, b);
            Add(c, Sink("erectedA")); Add(c, Sink("erectedB"));
            Connect(c, "subs", "yard");
            Connect(c, "blocksA", "bayA"); Connect(c, "blocksB", "bayB");
            Connect(c, "bayA", "erectedA"); Connect(c, "bayB", "erectedB");
            c.Lists.Add(new ListConfig { Id = "yardList", Name = "yardList" });
            Push(c, "yard", "yardList");
            Pull(c, "yardList", "bayA", "", port: 1);
            Pull(c, "yardList", "bayB", "", port: 1);
            Crane(c, "yard");
            return c;
        }

        /// <summary>
        /// a real deadlock through a list. The yard (capacity 1) pushes its sub-blocks to a list; two bays
        /// (Combiners, SingleLabel on <c>block</c>) hold blocks 3 and 2 and pull "the sub-block of my block" from it. Sub-block
        /// 1 arrives first, fills the yard and nobody wants it: the yard waits for the list's pullers, the bays wait for
        /// their components from the list, the sub-blocks' source is blocked behind the yard.
        /// </summary>
        public static SimulationConfig YardListDeadlock()
        {
            var c = New(1044, 200);
            Add(c, ScheduleSourceBy("blocksA", 1, 0.0, 0.0, i => ("block", "3")));
            Add(c, ScheduleSourceBy("blocksB", 1, 0.0, 0.0, i => ("block", "2")));
            Add(c, ScheduleSourceBy("subs", 3, 1.0, 1.0, i => ("block", (i + 1).ToString())));
            Add(c, Queue("yard", 1));
            var a = Bay("bayA", "blocksA", 1); a.Parameters["inputOrder"] = new List<string> { "blocksA", "" };
            var b = Bay("bayB", "blocksB", 1); b.Parameters["inputOrder"] = new List<string> { "blocksB", "" };
            Add(c, a); Add(c, b);
            Add(c, Sink("erectedA")); Add(c, Sink("erectedB"));
            Connect(c, "subs", "yard");
            Connect(c, "blocksA", "bayA"); Connect(c, "blocksB", "bayB");
            Connect(c, "bayA", "erectedA"); Connect(c, "bayB", "erectedB");
            c.Lists.Add(new ListConfig { Id = "yardList", Name = "yardList" });
            Push(c, "yard", "yardList");
            Pull(c, "yardList", "bayA", "", port: 1);
            Pull(c, "yardList", "bayB", "", port: 1);
            c.StopWhenEmpty = true;
            return c;
        }

        /// <summary>One bay area with two assemblies in parallel (Combiner capacity 2) instead of two bays: each assembly
        /// matches its own main item; the blocks arrive 3, 1, 2, 4 and the sub-blocks 1, 2, 3, 4.</summary>
        public static SimulationConfig YardKittingCapacity()
        {
            var c = New(1043, 200);
            Add(c, ScheduleSourceBy("blocks", 4, 0.0, 0.0, i => ("block", new[] { "3", "1", "2", "4" }[i])));
            Add(c, ScheduleSourceBy("subs", 4, 1.0, 0.0, i => ("block", (i + 1).ToString())));
            var yard = Queue("yard", 4);
            yard.Parameters["releaseMode"] = "AnyAccepted";
            Add(c, yard);
            var bays = Bay("bays", "blocks", 2);
            Add(c, bays);
            Add(c, Sink("erected"));
            Connect(c, "subs", "yard"); Connect(c, "blocks", "bays"); Connect(c, "yard", "bays"); Connect(c, "bays", "erected");
            Crane(c, "yard");
            return c;
        }

        // An assembly bay: a Combiner taking one sub-block of its main item's block (SingleLabel on "block", no value).
        /// <summary>
        /// the yard serves sub-blocks by key (<c>block</c>) to 2 bays through a
        /// list, and an overhead crane on axes (team travel <c>Axes</c>, real durations from the points' coordinates) carries
        /// each one from the yard to the bay that pulled it. Out-of-order blocks: bay A builds 3 then 1, bay B 2 then 4; the
        /// sub-blocks arrive 1, 2, 3, 4.
        /// </summary>
        public static SimulationConfig YardListAxes()
        {
            var c = New(1043, 500);
            c.Network = Points(("Y", 0, 0, 0), ("A", 20, 0, 0), ("B", 0, 15, 0));
            Add(c, ScheduleSourceBy("blocksA", 2, 0.0, 0.0, i => ("block", i == 0 ? "3" : "1")));
            Add(c, ScheduleSourceBy("blocksB", 2, 0.0, 0.0, i => ("block", i == 0 ? "2" : "4")));
            Add(c, ScheduleSourceBy("subs", 4, 1.0, 0.0, i => ("block", (i + 1).ToString())));
            Add(c, Placed(Queue("yard", 4), "Y"));
            var a = Bay("bayA", "blocksA", 1); a.Parameters["inputOrder"] = new List<string> { "blocksA", "" };
            var b = Bay("bayB", "blocksB", 1); b.Parameters["inputOrder"] = new List<string> { "blocksB", "" };
            Add(c, Placed(a, "A")); Add(c, Placed(b, "B"));
            Add(c, Sink("erectedA")); Add(c, Sink("erectedB"));
            Connect(c, "subs", "yard");
            Connect(c, "blocksA", "bayA"); Connect(c, "blocksB", "bayB");
            Connect(c, "bayA", "erectedA"); Connect(c, "bayB", "erectedB");
            c.Lists.Add(new ListConfig { Id = "yardList", Name = "yardList" });
            Push(c, "yard", "yardList");
            Pull(c, "yardList", "bayA", "", port: 1);
            Pull(c, "yardList", "bayB", "", port: 1);
            var t = new ResourceTeamConfig { Id = "crane", Name = "crane", Travel = "Axes", HomeNode = "Y",
                                             AxisSpeed = ResourceBuild.TripleSpec(2, 2, 1), AxisAccel = ResourceBuild.TripleSpec(1, 1, 1) };
            t.Members.Add(new ResourceMemberConfig { Name = "Crane1", LoadTime = 0.5, UnloadTime = 0.5 });
            c.ResourceTeams.Add(t);
            c.Elements.Find(x => x.Id == "yard").Parameters[ResourceUseKeys.TransportTeam] = "crane";
            c.StopWhenEmpty = true;
            return c;
        }

        private static ElementConfig Bay(string id, string mainSource, int capacity)
        {
            var bay = El(id, "UnityCombiner");
            bay.Parameters["requirements"] = new[] { 1 };
            bay.Parameters["capacity"] = capacity;
            bay.Parameters["pullModeType"] = "SingleLabel";
            bay.Parameters["singleLabelName"] = "block";
            bay.Parameters["singleLabelValue"] = "";
            bay.Parameters["delayStrategyType"] = "Constant";
            bay.Parameters["constantValue"] = 5.0;
            bay.Parameters["inputOrder"] = new List<string> { mainSource, "yard" };
            return bay;
        }

        // The crane: one abstract carrier, load 0.5, travel 2, unload 0.5.
        private static void Crane(SimulationConfig c, string holder)
        {
            Team(c, "crane", "", "", ("Crane1", new string[0]));
            var e = c.Elements.Find(x => x.Id == holder);
            e.Parameters[ResourceUseKeys.TransportTeam] = "crane";
            e.Parameters[ResourceUseKeys.TransportLoad] = "Constant~0.5";
            e.Parameters[ResourceUseKeys.TransportTravel] = "Constant~2";
            e.Parameters[ResourceUseKeys.TransportUnload] = "Constant~0.5";
        }

        // ── DTO helpers ──────────────────────────────────────────────────────

        private static SimulationConfig New(int seed, double horizon)
            => new SimulationConfig { UseFixedSeed = true, Seed = seed, MaxSimulationTime = horizon };

        private static ElementConfig El(string id, string type) => new ElementConfig(id, type, id);

        private static void Add(SimulationConfig c, ElementConfig e) => c.Elements.Add(e);

        private static void Connect(SimulationConfig c, string from, string to)
            => c.Connections.Add(new ConnectionConfig(from, to, "General"));

        /// <summary>Inter-arrival source with exponential gaps of the given mean.</summary>
        private static ElementConfig Source(string id, double meanGap)
        {
            var e = El(id, "UnitySource");
            e.Parameters["sourceMode"] = "InterArrival";
            e.Parameters["interArrivalStrategyType"] = "Exponential";
            e.Parameters["exponentialRate"] = 1.0 / meanGap;
            return e;
        }

        /// <summary>Schedule source: <paramref name="rows"/> arrivals every <paramref name="gap"/>, with fixed labels.</summary>
        private static ElementConfig ScheduleSource(string id, int rows, double gap, params (string col, string value)[] labels)
        {
            var table = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string>(), ["Name"] = new List<string>(), ["Q"] = new List<string>(),
            };
            foreach (var l in labels) table[l.col] = new List<string>();
            for (int i = 0; i < rows; i++)
            {
                table["Time"].Add((i * gap).ToString(System.Globalization.CultureInfo.InvariantCulture));
                table["Name"].Add("Container");
                table["Q"].Add("1");
                foreach (var l in labels) table[l.col].Add(l.value);
            }
            var e = El(id, "UnitySource");
            e.Parameters["sourceMode"] = "Schedule";
            e.Parameters["arrivalsTable"] = table;
            return e;
        }

        private static ElementConfig Queue(string id, int capacity)
        {
            var e = El(id, "UnityQueue");
            e.Parameters["capacity"] = capacity;
            return e;
        }

        private static ElementConfig Server(string id, int capacity, string dist, params (string key, double value)[] p)
        {
            var e = El(id, "UnityMultiServer");
            e.Parameters["capacity"] = capacity;
            e.Parameters["delayStrategyType"] = dist;
            foreach (var kv in p) e.Parameters[kv.key] = kv.value;
            return e;
        }

        private static ElementConfig Sink(string id) => El(id, "UnitySink");
    }
}
