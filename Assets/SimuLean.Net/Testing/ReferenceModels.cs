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
    public static partial class ReferenceModels
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
                Pair("resource_preemption", ResourcePreemption()),
                Pair("resource_preemption_everywhere", ResourcePreemptionEverywhere()),
                Pair("resource_primitives", ResourcePrimitives()),
                Pair("max_wait", MaxWait()),
                Pair("repair_crew", RepairCrew()),
                Pair("setup_changeover", SetupChangeover()),
                Pair("setup_matrix_grouping", SetupMatrixGrouping()),
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
                Pair("rate_table_clinic", RateTableClinic()),
                Pair("stall_combiner", StallCombiner()),
                Pair("deadlock_dead_end", DeadlockDeadEnd()),
                Pair("finish_with_surplus", FinishWithSurplus()),
                Pair("end_at_storage", EndAtStorage()),
                Pair("storage_stock_list_pull", StorageStockListPull()),
                Pair("line_side_unpacker", LineSideUnpacking()),
                Pair("combiner_separator", CombinerSeparator()),
                Pair("separator_split", SeparatorSplit()),
                Pair("pick_face_kanban", PickFaceKanban()),
                Pair("base_stock_truck", BaseStockTruck()),
                Pair("milk_run_two_tours", MilkRunTwoTours()),
                Pair("milk_run_shuttle_line", MilkRunShuttleLine()),
                Pair("paced_line_starvation", PacedLineStarvation()),
                Pair("call_off_buffer_line", CallOffBufferLine()),
                Pair("sequencing_station_lot", SequencingStationLot()),
                Pair("warehouse_to_line", WarehouseToLine()),
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

        // ── naval library ───────────────────────────────────────────────

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
            var e = El(id, "Source");
            e.Set("mode", "Schedule");
            e.Set("arrivalsTable", table);
            return e;
        }

        /// <summary>
        /// Erection in sequence: six blocks arrive out of order into a yard that releases any accepted item; the
        /// slipway takes only the next of the sequence (one front), the erection time grows with the block's weight, the
        /// crane is held only for its lift (`processBy.holdFor`); the complete hull leaves as one ship to a launch sink that
        /// keeps it.
        /// </summary>
        public static SimulationConfig ErectionSequence()
        {
            var c = New(1044, 500);
            Add(c, BlockSource("blocks", 3, (3, 3, "", 40), (1, 1, "", 55), (2, 2, "", 50), (5, 5, "", 45), (4, 4, "", 60), (6, 6, "", 35)));
            var yard = Queue("yard", 10);
            yard.Set("offerOrder", "AnyAccepted");
            Add(c, yard);
            var dock = El("slipway", "ErectionDock");
            dock.Set("capacity", 1);
            dock.Set("erectionRule", "Sequence");
            dock.Set("expectedBlocks", 6);
            dock.Set("delayStrategy.strategyType", "Expression");
            dock.Set("delayStrategy.expression", "2 + 0.05 * weight");
            Add(c, dock);
            var launch = Sink("launch");
            launch.Set("keepItems", true);
            Add(c, launch);
            Connect(c, "blocks", "yard"); Connect(c, "yard", "slipway"); Connect(c, "slipway", "launch");
            Team(c, "crane", "", "", ("Crane", new string[0]));
            ProcessBy(c, "slipway", "crane", "EndOfProcess", "");
            c.Elements.Find(x => x.Id == "slipway").Set("processBy.holdFor", "Constant~1");
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
            yard.Set("offerOrder", "AnyAccepted");
            Add(c, yard);
            var dock = El("dock", "ErectionDock");
            dock.Set("capacity", 2);
            dock.Set("erectionRule", "Precedence");
            dock.Set("delayStrategy.strategyType", "Constant");
            dock.Set("delayStrategy.constantValue", 6.0);
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
        /// The naval library's block process (a block-assembly flow on existing elements, generic data): sub-block kits ->
        /// sub-assembly (2 cells, fitters) -> turning when the sub-block is turned -> sub-block yard (any accepted) -> block
        /// assembly bays (Combiner, 2 in parallel, block = its nsub sub-blocks, welders) -> outfitting -> blasting and painting
        /// (the painter for 6 of its 10 h: processBy.holdFor) -> block yard -> slipway (sequence, crane for the lift only) ->
        /// launch (keeps the ship). The tables are the sample hull's generated parts.
        /// </summary>
        public static SimulationConfig NavalBlockProcess()
        {
            var c = New(1046, 2000);
            var hull = SampleHull();
            var release = new SimuLean.Naval.BlockBreakdown.ReleaseOptions { BlockInterval = 12 };
            ElementConfig TableSource(string id, SimuLean.Naval.BlockBreakdown.Part part)
            {
                var e = El(id, "Source");
                e.Set("mode", "Schedule");
                e.Set("arrivalsTable", hull.Generate(part, release));
                return e;
            }
            ElementConfig Station(string id, int capacity, string expression)
            {
                var e = El(id, "MultiServer");
                e.Set("capacity", capacity);
                e.Set("delayStrategy.strategyType", "Expression");
                e.Set("delayStrategy.expression", expression);
                return e;
            }
            Add(c, TableSource("kits", SimuLean.Naval.BlockBreakdown.Part.SubBlocks));
            Add(c, TableSource("orders", SimuLean.Naval.BlockBreakdown.Part.Blocks));
            var sba = Station("subassembly", 2, "has(t_sba) ? t_sba : 6");
            sba.Set("outputStrategy.strategyType", "Expression");
            sba.Set("outputStrategy.routingExpression", "item.turn == 1 ? 0 : 1");
            Add(c, sba);
            Add(c, Station("turning", 1, "2"));
            var yard = Queue("subblock_yard", 8);
            yard.Set("offerOrder", "AnyAccepted");
            Add(c, yard);
            var bays = El("block_bays", "Combiner");
            bays.Set("requirements", new[] { 1 });
            bays.Set("capacity", 2);
            bays.Set("pullModeType", "SingleLabel");
            bays.Set("singleLabelName", "block");
            bays.Set("singleLabelValue", "");
            bays.Set("updateRequirements", true);
            bays.Set("updateLabels", new List<string> { "nsub" });
            bays.Set("delayStrategy.strategyType", "Expression");
            bays.Set("delayStrategy.expression", "has(t_ba) ? t_ba : 16");
            bays.Set("inputOrder", new List<string> { "orders", "subblock_yard" });
            Add(c, bays);
            Add(c, Station("outfitting", 2, "8"));
            Add(c, Station("painting", 1, "10"));
            var blockYard = Queue("block_yard", 6);
            blockYard.Set("offerOrder", "AnyAccepted");
            Add(c, blockYard);
            var slipway = El("slipway", "ErectionDock");
            slipway.Set("capacity", 1);
            slipway.Set("erectionRule", "Sequence");
            slipway.Set("expectedBlocks", hull.BlockCount);
            slipway.Set("delayStrategy.strategyType", "Expression");
            slipway.Set("delayStrategy.expression", "4 + 0.02 * weight");
            Add(c, slipway);
            var launch = Sink("launch");
            launch.Set("keepItems", true);
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
            Team(c, "fitters", "", "", ("Fitter1", new[] { "skill='fitter'" }), ("Fitter2", new[] { "skill='fitter'" }));
            Team(c, "welders", "", "", ("Welder1", new[] { "skill='welder'" }), ("Welder2", new[] { "skill='welder'" }),
                 ("Welder3", new[] { "skill='welder'" }), ("Welder4", new[] { "skill='welder'" }));
            Team(c, "painters", "", "", ("Painter1", new[] { "skill='painter'" }));
            Team(c, "crane", "", "", ("SlipwayCrane", new string[0]));
            ProcessBy(c, "subassembly", "fitters", "EndOfProcess", "");
            ProcessBy(c, "block_bays", "welders", "EndOfProcess", "", Requirement(2, "", "welders"));
            ProcessBy(c, "painting", "painters", "EndOfProcess", "");
            c.Elements.Find(x => x.Id == "painting").Set("processBy.holdFor", "Constant~6");
            ProcessBy(c, "slipway", "crane", "EndOfProcess", "");
            c.Elements.Find(x => x.Id == "slipway").Set("processBy.holdFor", "Constant~1");
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
            src.Set("outputStrategy.strategyType", "RoundRobin");
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
            src.Set("outputStrategy.strategyType", "ShortestQueue");
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
            src.Set("outputStrategy.strategyType", "ShortestQueue");
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
            m1.Set("transportBy.team", "crew");
            m1.Set("transportBy.loadTime", "Constant~0.2");
            m1.Set("transportBy.travelTime", "Uniform~1~2");
            m1.Set("transportBy.unloadTime", "Constant~0.2");
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
            var hi = Source("srcHigh", 3.0); hi.Set("createLabels", new List<string> { "prio=5" });
            var lo = Source("srcLow", 2.0); lo.Set("createLabels", new List<string> { "prio=1" });
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

        /// <summary>Preemption: a team of two operators shared by routine work (priority 1, never preempts), an urgent
        /// station (priority 5, Resume: takes an operator from routine work, which goes on later with its remaining time) and
        /// a rework station (priority 3, Restart: takes one from routine work, which starts again; it cannot take one from the
        /// urgent station). Requests ordered by priority.</summary>
        public static SimulationConfig ResourcePreemption()
        {
            var c = New(1056, 2000);
            Add(c, Source("srcRoutine", 1.2)); Add(c, Source("srcUrgent", 8.0)); Add(c, Source("srcRework", 6.0));
            Add(c, Queue("qRoutine", 1000)); Add(c, Queue("qUrgent", 1000)); Add(c, Queue("qRework", 1000));
            Add(c, Server("routine", 2, "Exponential", ("exponentialRate", 1.0 / 1.6)));
            Add(c, Server("urgent", 1, "Constant", ("constantValue", 1.0)));
            Add(c, Server("rework", 1, "Uniform", ("uniformMin", 0.5), ("uniformMax", 1.5)));
            Add(c, Sink("sink"));
            foreach (var k in new[] { "Routine", "Urgent", "Rework" })
            {
                Connect(c, "src" + k, "q" + k); Connect(c, "q" + k, k.ToLowerInvariant()); Connect(c, k.ToLowerInvariant(), "sink");
            }
            Team(c, "ops", "|priority desc", "", ("Op1", new string[0]), ("Op2", new string[0]));
            ProcessBy(c, "routine", "ops", "EndOfProcess", "");
            ProcessBy(c, "urgent", "ops", "EndOfProcess", "5");
            ProcessBy(c, "rework", "ops", "EndOfProcess", "3");
            c.Elements.Find(x => x.Id == "urgent").Set("processBy.preemption", "Resume");
            c.Elements.Find(x => x.Id == "rework").Set("processBy.preemption", "Restart");
            return c;
        }

        /// <summary>Preemption everywhere: a team of two operators shared by an assembly station (one request per
        /// task, priority 1), a painting booth whose operator works its own time and leaves the item to cure (HoldFor,
        /// Restart on its stop) and an urgent station (priority 5, Resume) that takes an operator from an assembly task (only
        /// that task waits, then goes on with its remaining time) or from the painter's own time (another finishes it).
        /// Requests ordered by priority.</summary>
        public static SimulationConfig ResourcePreemptionEverywhere()
        {
            var c = New(1062, 2000);
            Add(c, Source("jobs", 8.0)); Add(c, Source("stock", 5.0)); Add(c, Source("srcPaint", 6.0)); Add(c, Source("srcUrgent", 9.0));
            var asy = El("asy", "AssemblyStation");
            asy.Set("workerCount", 2);
            asy.Set("stockCapacity", 10);
            asy.Set("tasks", new List<AssemblyTaskSettings> { AssemblyTaskSettings.Of("a", SamplerSettings.Exponential(0.5), 0), AssemblyTaskSettings.Of("b", SamplerSettings.Constant(2), 0, new[] { "a" }, "Item:1"), AssemblyTaskSettings.Of("c", SamplerSettings.Uniform(1, 3), 1) });
            Add(c, asy);
            Add(c, Queue("qPaint", 1000)); Add(c, Queue("qUrgent", 1000));
            Add(c, Server("paint", 1, "Uniform", ("uniformMin", 3.0), ("uniformMax", 5.0)));
            Add(c, Server("urgent", 1, "Constant", ("constantValue", 1.5)));
            Add(c, Sink("sink"));
            Connect(c, "jobs", "asy"); Connect(c, "asy", "sink");
            c.Connections.Add(new ConnectionConfig("stock", "asy", "StockInput"));
            Connect(c, "srcPaint", "qPaint"); Connect(c, "qPaint", "paint"); Connect(c, "paint", "sink");
            Connect(c, "srcUrgent", "qUrgent"); Connect(c, "qUrgent", "urgent"); Connect(c, "urgent", "sink");
            Team(c, "ops", "|priority desc", "", ("Op1", new string[0]), ("Op2", new string[0]));
            ProcessBy(c, "asy", "ops", "EndOfProcess", "");
            ProcessBy(c, "paint", "ops", "EndOfProcess", "");
            ProcessBy(c, "urgent", "ops", "EndOfProcess", "5");
            var paint = c.Elements.Find(x => x.Id == "paint");
            paint.Set("processBy.holdFor", "Constant~2");
            paint.Set("processBy.preemption", "Restart");
            c.Elements.Find(x => x.Id == "urgent").Set("processBy.preemption", "Resume");
            return c;
        }

        /// <summary>The resource primitives: a cutting station of two slots secures its
        /// operator before it takes a part (<c>processBy.acquireBeforeEntry</c>) and starts a job only when a place in
        /// the small buffer after it can be reserved (<c>reserveExit</c>); the lift needs a crane and then a driver of
        /// another team, the crane held while the driver is waited for (<c>processBy.acquire</c> = InOrder); the drivers
        /// also haul on their own.</summary>
        public static SimulationConfig ResourcePrimitives()
        {
            var c = New(1063, 2000);
            Add(c, Source("src", 4.0)); Add(c, Source("srcHaul", 10.0));
            Add(c, Queue("q", 1000)); Add(c, Queue("buf", 2)); Add(c, Queue("qHaul", 1000));
            var cut = Server("cut", 2, "Uniform", ("uniformMin", 2.0), ("uniformMax", 4.0));
            cut.Set("reserveExit", true);
            Add(c, cut);
            Add(c, Server("lift", 1, "Uniform", ("uniformMin", 1.5), ("uniformMax", 3.0)));
            Add(c, Server("haul", 1, "Constant", ("constantValue", 3.0)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "cut"); Connect(c, "cut", "buf"); Connect(c, "buf", "lift"); Connect(c, "lift", "sink");
            Connect(c, "srcHaul", "qHaul"); Connect(c, "qHaul", "haul"); Connect(c, "haul", "sink");
            Team(c, "ops", "", "", ("Op1", new string[0]), ("Op2", new string[0]));
            Team(c, "cranes", "", "", ("Crane1", new string[0]));
            Team(c, "drivers", "", "", ("Driver1", new string[0]));
            ProcessBy(c, "cut", "ops", "EndOfProcess", "");
            c.Elements.Find(x => x.Id == "cut").Set("processBy.acquireBeforeEntry", true);
            ProcessBy(c, "lift", "cranes", "EndOfProcess", "", Requirement(1, "", "crane"), RequirementSettings.Of(1, "", "driver", "drivers"));
            c.Elements.Find(x => x.Id == "lift").Set("processBy.acquire", "InOrder");
            ProcessBy(c, "haul", "drivers", "EndOfProcess", "");
            return c;
        }

        /// <summary>Max-wait timers: customers renege — an item that waited 8 in the queue leaves for 'lost'
        /// (<c>maxWait</c>, <c>onTimeout</c> = Reroute, <c>timeoutTarget</c>); the queue feeds a cutting station that secures
        /// its operator before the part and a saw without one, so a granted operator may wait HELD for a part that went
        /// to the saw: after 4 it gives up (<c>processBy.maxWait</c>, GiveUp) and is free for the packing line, which shares the
        /// operator; packing waits at most 6 for one and only records it (KeepWaiting).</summary>
        public static SimulationConfig MaxWait()
        {
            var c = New(1071, 2000);
            Add(c, Source("src", 2.5)); Add(c, Source("srcPack", 5.0));
            var q = Queue("q", 1000);
            q.Set("maxWait", "8");
            q.Set("onTimeout", "Reroute");
            q.Set("timeoutTarget", "lost");
            Add(c, q); Add(c, Queue("qPack", 1000));
            Add(c, Server("cut", 1, "Uniform", ("uniformMin", 1.0), ("uniformMax", 3.0)));
            Add(c, Server("saw", 1, "Uniform", ("uniformMin", 3.0), ("uniformMax", 6.0)));
            Add(c, Server("pack", 1, "Uniform", ("uniformMin", 1.5), ("uniformMax", 3.5)));
            Add(c, Sink("sink")); Add(c, Sink("lost"));
            Connect(c, "src", "q"); Connect(c, "q", "cut"); Connect(c, "q", "saw"); Connect(c, "cut", "sink"); Connect(c, "saw", "sink");
            Connect(c, "srcPack", "qPack"); Connect(c, "qPack", "pack"); Connect(c, "pack", "sink");
            Team(c, "ops", "", "", ("Op1", new string[0]));
            ProcessBy(c, "cut", "ops", "EndOfProcess", "");
            ProcessBy(c, "pack", "ops", "EndOfProcess", "");
            var cut = c.Elements.Find(x => x.Id == "cut");
            cut.Set("processBy.acquireBeforeEntry", true);
            cut.Set("processBy.maxWait", "4");
            cut.Set("processBy.onTimeout", "GiveUp");
            var pack = c.Elements.Find(x => x.Id == "pack");
            pack.Set("processBy.maxWait", "3");
            return c;
        }

        /// <summary>Repair crews: two machines that fail (MTBF / MTTR) and two operators that break down too, all
        /// repaired by one mechanic — a machine's repair has priority 5 and takes the mechanic from an operator's repair
        /// (priority 3, preemptable, Resume); the stations' use can be interrupted (Resume), so a broken operator hands its
        /// job over. Each stop lasts the wait for the mechanic plus the repair (WAITING_FOR_REPAIR, then BREAKDOWN).</summary>
        public static SimulationConfig RepairCrew()
        {
            var c = New(1057, 3000);
            Add(c, Source("src", 1.1));
            Add(c, Queue("q", 1000));
            Add(c, Server("m1", 1, "Exponential", ("exponentialRate", 1.0 / 1.6)));
            Add(c, Server("m2", 1, "Exponential", ("exponentialRate", 1.0 / 1.6)));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "m1"); Connect(c, "q", "m2"); Connect(c, "m1", "sink"); Connect(c, "m2", "sink");
            Team(c, "ops", "", "", ("Op1", new string[0]), ("Op2", new string[0]));
            Team(c, "mech", "|priority desc", "", ("Mechanic", new string[0]));
            foreach (var m in new[] { "m1", "m2" })
            {
                ProcessBy(c, m, "ops", "EndOfProcess", "");
                c.Elements.Find(x => x.Id == m).Set("processBy.preemption", "Resume");
            }
            var fail = new DowntimeConfig("machineFail", "MtbfMttr", "m1", "m2");
            fail.Set("timeToFailure", "ExponentialMean~120");
            fail.Set("timeToRepair", "ExponentialMean~9");
            fail.Set("repairBy.team", "mech");
            fail.Set("repairBy.priority", "5");
            fail.Set("repairBy.preemption", "Resume");
            c.Downtimes.Add(fail);
            var opFail = new DowntimeConfig("operatorFail", "MtbfMttr", "ops");
            opFail.Set("timeToFailure", "ExponentialMean~200");
            opFail.Set("timeToRepair", "ExponentialMean~12");
            opFail.Set("repairBy.team", "mech");
            opFail.Set("repairBy.priority", "3");
            opFail.Set("repairBy.preemptable", true);
            c.Downtimes.Add(opFail);
            return c;
        }

        /// <summary>Setups: two product types (labels <c>type</c> 1 and 2) through a press that changes over on a
        /// type change (Uniform 0.5–1.5, first item included) done by a setter of its own team, then a paint booth that sets up
        /// before every item (0.2) with the operator of its process, kept for the process (one request per job).</summary>
        public static SimulationConfig SetupChangeover()
        {
            var c = New(1058, 2000);
            var a = Source("srcA", 4.0); a.Set("createLabels", new List<string> { "type=1" });
            var b = Source("srcB", 4.0); b.Set("createLabels", new List<string> { "type=2" });
            Add(c, a); Add(c, b);
            Add(c, Queue("q", 1000));
            Add(c, Server("press", 1, "Exponential", ("exponentialRate", 1.0)));
            Add(c, Server("paint", 1, "Constant", ("constantValue", 1.2)));
            Add(c, Sink("sink"));
            Connect(c, "srcA", "q"); Connect(c, "srcB", "q"); Connect(c, "q", "press"); Connect(c, "press", "paint"); Connect(c, "paint", "sink");
            Team(c, "setters", "", "", ("Setter", new string[0]));
            Team(c, "ops", "", "", ("Painter", new string[0]));
            var press = c.Elements.Find(x => x.Id == "press");
            press.Set("setup.when", "LabelChange"); press.Set("setup.label", "type"); press.Set("setup.setupFirst", true);
            press.Set("setup.time", "Uniform~0.5~1.5"); press.Set("setup.setupBy.team", "setters");
            ProcessBy(c, "paint", "ops", "EndOfProcess", "");
            var paint = c.Elements.Find(x => x.Id == "paint");
            paint.Set("setup.when", "EachItem"); paint.Set("setup.time", "Constant~0.2"); paint.Set("setup.setupBy.team", "ops"); paint.Set("setup.keepForProcess", true);
            return c;
        }

        /// <summary>Sequence-dependent setups: three product types (<c>type</c> A, B, C) through a queue that
        /// groups them by the cell's setup (at most 4 in a row ahead of the oldest) into a cell that changes over on a type
        /// change, its times from the setup matrix <c>changeovers</c> (first setup 3; A ↔ B Uniform 1–2; to C Triangular
        /// 2–3–5; from C 2.5; any other pair 1.5).</summary>
        public static SimulationConfig SetupMatrixGrouping()
        {
            var c = New(1059, 2000);
            foreach (string t in new[] { "A", "B", "C" })
            {
                var s = Source("src" + t, 4.5);
                s.Set("createLabels", new List<string> { "type='" + t + "'" });
                Add(c, s);
            }
            var q = Queue("q", 1000);
            q.Set("groupBySetup", true);
            q.Set("groupLimit", 4);
            Add(c, q);
            Add(c, Server("cell", 1, "Exponential", ("exponentialRate", 1.0)));
            Add(c, Sink("sink"));
            foreach (string t in new[] { "A", "B", "C" }) Connect(c, "src" + t, "q");
            Connect(c, "q", "cell"); Connect(c, "cell", "sink");
            var cell = c.Elements.Find(x => x.Id == "cell");
            cell.Set("setup.when", "LabelChange"); cell.Set("setup.label", "type"); cell.Set("setup.setupFirst", true);
            cell.Set("setup.time", "Constant~1.5"); cell.Set("setup.matrix", "changeovers");
            c.Tables.Add(new DataTableConfig
            {
                Name = "changeovers",
                Columns = new Dictionary<string, List<string>>
                {
                    ["from"] = new List<string> { "", "A", "B", "*", "C" },
                    ["to"] = new List<string> { "*", "B", "A", "C", "*" },
                    ["time"] = new List<string> { "3", "Uniform~1~2", "Uniform~1~2", "Triangular~2~3~5", "2.5" },
                },
            });
            return c;
        }

        private static void Team(SimulationConfig c, string id, string requestOrder, string resourceOrder,
                                 params (string name, string[] labels)[] members)
        {
            var t = new ResourceTeamConfig { Id = id, Name = id, RequestOrder = requestOrder, ResourceOrder = resourceOrder };
            foreach (var m in members) t.Members.Add(new ResourceMemberConfig { Name = m.name, Properties = new List<string>(m.labels) });
            c.ResourceTeams.Add(t);
        }

        private static RequirementSettings Requirement(int count, string query, string label) => RequirementSettings.Of(count, query, label);

        private static void ProcessBy(SimulationConfig c, string element, string team, string release, string priority,
                                      params RequirementSettings[] requirements)
        {
            var e = c.Elements.Find(x => x.Id == element);
            e.Set("processBy.team", team);
            e.Set("processBy.release", release);
            if (!string.IsNullOrEmpty(priority)) e.Set("processBy.priority", priority);
            if (requirements.Length > 0) e.Set("processBy.requirements", new List<RequirementSettings>(requirements));
        }

        /// <summary>Main + two component sources → Combiner → Sink: connection-order input mapping.</summary>
        public static SimulationConfig CombinerModel()
        {
            var c = New(1005, 2000);
            Add(c, Source("main", 4.0));
            Add(c, Source("compA", 1.5));
            Add(c, Source("compB", 3.0));
            var comb = El("comb", "Combiner");
            comb.Set("requirements", new[] { 2, 1 });
            comb.Set("batchMode", true);
            comb.Set("delayStrategy.strategyType", "Constant");
            comb.Set("delayStrategy.constantValue", 2.0);
            comb.Set("inputOrder", new List<string> { "main", "compA", "compB" });
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
            var ps = El("pick", "PickingStation");
            ps.Set("stockCapacity", 20);
            ps.Set("useContainerLabels", true);
            ps.Set("labelPrefix", "pick_");
            ps.Set("delayStrategy.strategyType", "Constant");
            ps.Set("delayStrategy.constantValue", 1.5);
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
            var asy = El("asy", "AssemblyStation");
            asy.Set("workerCount", 2);
            asy.Set("stockCapacity", 10);
            asy.Set("tasks", new List<AssemblyTaskSettings> { AssemblyTaskSettings.Of("a", SamplerSettings.Exponential(0.25), 0), AssemblyTaskSettings.Of("b", SamplerSettings.Constant(2), 0, new[] { "a" }, "Item:1"), AssemblyTaskSettings.Of("c", SamplerSettings.Uniform(1, 3), 1) });
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
            var st = El("st", "Stacker");
            st.Settings = new StackerSettings
            {
                PositionCount = 2, Capacity = 4, SelectionRule = StackSelectionRule.RoundRobin, CycleTime = SamplerSettings.Constant(0.5),
            };
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
            mx.Entries.Add(TravelMatrixEntryConfig.Of("H", "S1", 5));
            mx.Entries.Add(TravelMatrixEntryConfig.Of("H", "S2", 8));
            mx.Entries.Add(TravelMatrixEntryConfig.Of("S1", "S2", 6));
            c.TravelMatrices.Add(mx);
            Add(c, Source("src", 12.0));
            Add(c, Queue("q", 100));
            Add(c, Placed(Server("m1", 1, "Uniform", ("uniformMin", 2.0), ("uniformMax", 4.0)), "S1"));
            Add(c, Placed(Queue("buf", 3), "S2"));
            Add(c, Placed(Server("m2", 1, "Uniform", ("uniformMin", 2.0), ("uniformMax", 5.0)), "S2"));
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "m1"); Connect(c, "m1", "buf"); Connect(c, "buf", "m2"); Connect(c, "m2", "sink");
            var t = new ResourceTeamConfig { Id = "ops", Name = "ops", ResourceOrder = "|distance", Travel = TravelKind.Matrix, Matrix = "floor", Speed = 1.2, HomeNode = "H" };
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
            e.Set("walkBy.speed", 1.2);
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
            var t = new ResourceTeamConfig { Id = "robot", Name = "robot", Travel = TravelKind.Axes, HomeNode = "Park",
                                             AxisSpeed = new[] { 1, 1, 0.5 }, AxisAccel = new double[] { 2, 2, 1 } };
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
            var st = El("st", "Stacker");
            st.Settings = new StackerSettings
            {
                PositionCount = 2, Capacity = 5, SelectionRule = StackSelectionRule.RoundRobin,
                PickNode = "K", PositionNodes = new List<string> { "P0", "P1" },
            };
            Add(c, st);
            Add(c, Sink("sink"));
            Connect(c, "src", "q"); Connect(c, "q", "st"); Connect(c, "st", "sink");
            var t = new ResourceTeamConfig { Id = "gantry", Name = "gantry", Travel = TravelKind.Axes, HomeNode = "K",
                                             AxisSpeed = new[] { 1.5, 1, 0.5 }, AxisAccel = new double[] { 1, 1, 1 } };
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
            var t = new ResourceTeamConfig { Id = "ops", Name = "ops", ResourceOrder = "|distance", Travel = TravelKind.Grid, WalkGrid = "floor", Speed = 1.2, HomeNode = "H" };
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
            t.Travel = TravelKind.Calibrated; t.CalibrationBase = TravelKind.Matrix; t.CalibrationFit = new[] { 0.8, 0.85, 0.4 };
            t.CalibrationPairs.Add(new CalibrationPairSettings { From = "S1", To = "S2", Loaded = true, Mean = 6.9, Sd = 0.6, N = 12 });
            t.CalibrationPairs.Add(new CalibrationPairSettings { From = "S2", To = "S1", Loaded = false, Mean = 6.1, Sd = 0.5, N = 11 });
            t.CalibrationPairs.Add(new CalibrationPairSettings { From = "H", To = "S1", Loaded = false, Mean = 5.2, Sd = 0.4, N = 2 });     // too few samples: general model
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
            foreach (var p in pts) n.Nodes.Add(NetworkNodeConfig.Of(p.id, p.x, p.y, p.z));
            return n;
        }

        // A straight network: nodes at x along a line, consecutive nodes joined by two-way travel tracks.
        private static NetworkConfig Network(params (string id, double x)[] nodes)
        {
            var n = new NetworkConfig();
            for (int i = 0; i < nodes.Length; i++)
            {
                n.Nodes.Add(NetworkNodeConfig.Of(nodes[i].id, nodes[i].x, 0, 0));
                if (i > 0)
                    n.Tracks.Add(NetworkTrackConfig.Of(nodes[i - 1].id, nodes[i].id,
                        Math.Abs(nodes[i].x - nodes[i - 1].x), true, Transport.TrackType.TravelPath));
            }
            return n;
        }

        private static ElementConfig Placed(ElementConfig e, string node)
        {
            e.Set("location", node);
            return e;
        }

        // A team of vehicles on the network (load / unload 1 each).
        private static void Fleet(SimulationConfig c, string id, string home, double speed, string resourceOrder, int vehicles)
        {
            var t = new ResourceTeamConfig { Id = id, Name = id, ResourceOrder = resourceOrder, Travel = TravelKind.Network, Speed = speed, HomeNode = home };
            for (int i = 1; i <= vehicles; i++) t.Members.Add(new ResourceMemberConfig { Name = id + "_" + i, LoadTime = 1, UnloadTime = 1 });
            c.ResourceTeams.Add(t);
        }

        private static void TransportBy(SimulationConfig c, string element, string team)
            => c.Elements.Find(x => x.Id == element).Set("transportBy.team", team);

        /// <summary>Basic line whose server fails (MTBF/MTTR): pausable work and downtime accounting.</summary>
        public static SimulationConfig DowntimeModel()
        {
            var c = BasicLine();
            c.Seed = 1010;
            var dt = new DowntimeConfig("fail", "MtbfMttr", "srv");
            dt.Set("timeToFailure", "ExponentialMean~60");
            dt.Set("timeToRepair", "ExponentialMean~6");
            c.Downtimes.Add(dt);
            return c;
        }

        /// <summary>
        /// Source → a two-lane accumulation buffer limited by length (lanes 6 and 4, gap 0.5, items 1 or 2.5 long by
        /// their <c>length</c> label) → Server → Sink.
        /// </summary>
        public static SimulationConfig LengthLimitedLanes()
        {
            var c = New(1013, 2000);
            var src = Source("src", 1.0);
            src.Set("createLabels", new List<string> { "length=1" });
            Add(c, src);
            var src2 = Source("src2", 4.0);
            src2.Set("createLabels", new List<string> { "length=2.5" });
            Add(c, src2);
            var lanes = El("lanes", "LengthLimitedQueue");
            lanes.Settings = new LengthLimitedQueueSettings { LaneLengths = new[] { 6.0, 4.0 }, Gap = 0.5 };
            Add(c, lanes);
            Add(c, Server("srv", 1, "Exponential", ("exponentialRate", 1.6)));
            Add(c, Sink("sink"));
            Connect(c, "src", "lanes"); Connect(c, "src2", "lanes"); Connect(c, "lanes", "srv"); Connect(c, "srv", "sink");
            return c;
        }

        /// <summary>
        /// One week in minutes from Monday 2026-10-05 00:00; the server works Mon–Fri 06:00–14:00 (a shift downtime,
        /// 40 h of 168 h), arrivals all week.
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
            shift.Set("shiftPattern", "Mon-Fri 06:00-14:00");
            c.Downtimes.Add(shift);
            return c;
        }

        /// <summary>
        /// Twenty one-hour jobs, all there at the start, for one station on a day shift (Mon–Fri 06:00–14:00), stop when
        /// empty (test only, not a baseline): at 14:00 only the calendar's next tick is pending, which is no stall — the
        /// shift starts again in the morning, and the run ends empty on the third day.
        /// </summary>
        public static SimulationConfig ShiftNightModel()
        {
            var c = New(1046, 7 * 24 * 60);
            c.StartDateTime = "2026-10-05 06:00";
            c.TimeUnitSeconds = 60;
            Add(c, ScheduleSourceBy("jobs", 20, 0.0, 0.0, i => ("job", (i + 1).ToString())));
            Add(c, Queue("q", 100));
            Add(c, Server("srv", 1, "Constant", ("constantValue", 60.0)));
            Add(c, Sink("sink"));
            Connect(c, "jobs", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
            var shift = new DowntimeConfig("shift", "Shift", "srv");
            shift.Set("shiftPattern", "Mon-Fri 06:00-14:00");
            c.Downtimes.Add(shift);
            c.StopWhenEmpty = true;
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
            var low = Source("lowParts", 4.0);  low.Set("createLabels", new List<string> { "prio=1" });
            var high = Source("highParts", 6.0); high.Set("createLabels", new List<string> { "prio=5" });
            Add(c, low); Add(c, high);
            Add(c, Queue("lowQ", 1000));
            Add(c, Queue("highQ", 1000));
            var comb = El("comb", "Combiner");
            comb.Set("requirements", new[] { 2 });
            comb.Set("batchMode", true);
            comb.Set("delayStrategy.strategyType", "Constant");
            comb.Set("delayStrategy.constantValue", 1.5);
            comb.Set("inputOrder", new List<string> { "main", "" });
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
            conn.Query = query;
            if (port >= 0) conn.Input = port;
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
            var e = El(id, "Source");
            e.Set("mode", "Schedule");
            e.Set("arrivalsTable", table);
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

        /// <summary>A walk-in clinic whose arrivals follow a rate table (a Poisson process with a rate per period:
        /// 0.1 / min for 2 h, 0.25 / min for 2 h, the door closed after 4 h), two doctors; the run ends when the model is empty,
        /// at its last exit.</summary>
        public static SimulationConfig RateTableClinic()
        {
            var c = New(1032, 5000);
            var src = El("walkins", "Source");
            src.Set("mode", "RateTable");
            src.Set("ratePeriods", new List<RatePeriodSettings>
            {
                RatePeriodSettings.Of(0, 0.1, "slot = 'morning'"), RatePeriodSettings.Of(120, 0.25, "slot = 'midday'"), RatePeriodSettings.Of(240, 0),
            });
            Add(c, src);
            Add(c, Queue("waiting", 1000));
            Add(c, Server("doctors", 2, "Exponential", ("exponentialRate", 0.125)));
            Add(c, Sink("out"));
            Connect(c, "walkins", "waiting"); Connect(c, "waiting", "doctors"); Connect(c, "doctors", "out");
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
            var comb = El("comb", "Combiner");
            comb.Set("requirements", new[] { 2 });
            comb.Set("delayStrategy.strategyType", "Constant");
            comb.Set("delayStrategy.constantValue", 1.0);
            comb.Set("inputOrder", new List<string> { "main", "comp" });
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
            var asm = El("assembly", "Combiner");
            asm.Set("requirements", new[] { 1 });
            asm.Set("delayStrategy.strategyType", "Constant");
            asm.Set("delayStrategy.constantValue", 4.0);
            asm.Set("inputOrder", new List<string> { "blocks", "rack" });
            Add(c, asm);
            Add(c, Queue("hull1", 2));
            Add(c, Queue("hull2", 2));
            Connect(c, "blocks", "assembly"); Connect(c, "kits", "rack"); Connect(c, "rack", "assembly");
            Connect(c, "assembly", "hull1"); Connect(c, "assembly", "hull2");
            c.StopWhenEmpty = true;
            return c;
        }

        /// <summary>The same yard ended by the hull instead: stop when the two hull queues have received the 4 blocks.</summary>
        public static SimulationConfig EndAtStorage()
        {
            var c = FinishWithSurplus();
            c.StopWhenEmpty = false;
            c.StopAt.Add("hull1");
            c.StopAt.Add("hull2");
            c.StopAtCount = 4;
            return c;
        }

        // ── published storage stock ────────────────────

        /// <summary>
        /// A storage publishes its stock to the list <c>stock</c>; a queue at node <c>face</c> (before a 100-per-item station)
        /// pulls <c>sku == A</c>. The forklift (network dock 0 — n1 10 — n2 20 — face 30, 1 m/s, load / unload 1) fetches each
        /// pulled item and carries it to the queue. A at 0 → delivered 32; A at 40 → 82; A at 50 and B at 60 go to lane L2 (L1
        /// is being emptied), B in front (LIFO). The station frees the queue at 132: B is re-homed n2 → L1 (154), then A is
        /// carried n2 → face (176). Hand-computed in <c>warehousetests</c>.
        /// </summary>
        public static SimulationConfig StorageStockListPull()
        {
            var c = New(1030, 400);
            c.Network = Network(("dock", 0), ("n1", 10), ("n2", 20), ("face", 30));
            Add(c, SkuArrivals("in", (0, "A"), (40, "A"), (50, "A"), (60, "B")));
            var wh = Placed(StorageEl("wh", SimuLean.Storage.AccessDiscipline.Lifo, ("L1", 10, 2, "n1"), ("L2", 20, 2, "n2")), "dock");
            wh.Set("transportBy.team", "forklift");
            Add(c, wh);
            Add(c, Placed(Queue("q", 1), "face"));
            Add(c, Server("station", 1, "Constant", ("constantValue", 100.0)));
            Add(c, Sink("sink"));
            Connect(c, "in", "wh"); Connect(c, "q", "station"); Connect(c, "station", "sink");
            Fleet(c, "forklift", "dock", 1.0, "", 1);
            c.Lists.Add(new ListConfig { Id = "stock", Name = "stock" });
            Push(c, "wh", "stock");
            Pull(c, "stock", "q", "sku == A");
            return c;
        }

        /// <summary>
        /// Source (one part every 1) → stacker building wagons of 4 → line-side unpacker (2 wagons, 1.5 per piece, 2 to
        /// change wagon) → sink; the empty wagons leave through its port <c>empties</c> to their own sink. The unpacker is
        /// the bottleneck (a wagon takes 4 × 1.5 + 2 = 8 for 4 pieces against 4 arriving), so the stacker blocks and the
        /// pieces out ≈ horizon / 2.
        /// </summary>
        public static SimulationConfig LineSideUnpacking()
        {
            var c = New(1101, 400);
            var src = El("src", "Source");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", 1.0);
            Add(c, src);
            var st = El("wagons", "Stacker");
            st.Settings = new StackerSettings { PositionCount = 1, Capacity = 4, StackType = "Wagon" };
            Add(c, st);
            var u = El("unp", "LineSideUnpacker");
            u.Settings = new LineSideUnpackerSettings { Capacity = 2, PieceTime = SamplerSettings.Constant(1.5), ChangeTime = SamplerSettings.Constant(2) };
            Add(c, u);
            Add(c, Sink("sink"));
            Add(c, Sink("empties"));
            Connect(c, "src", "wagons"); Connect(c, "wagons", "unp"); Connect(c, "unp", "sink");
            var port = new ConnectionConfig("unp", "empties", "PortOutput");
            port.Port = Separator.PortEmpties;
            c.Connections.Add(port);
            return c;
        }

        /// <summary>
        /// The inverse of the Combiner: a main item every 5 and a part every 2 → a Combiner in batch mode (2 parts on each
        /// main item, 1) → a separator unpacking it (1, all together): the parts to their sink, the main items through
        /// port <c>empties</c> to theirs — 2 parts per main item come back out.
        /// </summary>
        public static SimulationConfig CombinerSeparator()
        {
            var c = New(1103, 500);
            Add(c, Source("main", 5.0));
            Add(c, Source("part", 2.0));
            var comb = El("comb", "Combiner");
            comb.Set("requirements", new[] { 2 });
            comb.Set("batchMode", true);
            comb.Set("delayStrategy.strategyType", "Constant");
            comb.Set("delayStrategy.constantValue", 1.0);
            comb.Set("inputOrder", new List<string> { "main", "part" });
            Add(c, comb);
            var sep = El("sep", "Separator");
            sep.Settings = new SeparatorSettings { ProcessTime = SamplerSettings.Constant(1) };
            Add(c, sep);
            Add(c, Sink("parts"));
            Add(c, Sink("mains"));
            Connect(c, "main", "comb"); Connect(c, "part", "comb"); Connect(c, "comb", "sep"); Connect(c, "sep", "parts");
            var port = new ConnectionConfig("sep", "mains", "PortOutput");
            port.Port = Separator.PortEmpties;
            c.Connections.Add(port);
            return c;
        }

        /// <summary>
        /// Source (one order every 4, Exponential) → separator splitting each order into 3 (process time 2, all together)
        /// → a 2-server processor (Uniform 1–3) → sink: input = output = 3 × orders, of which 2 per order are split
        /// copies.
        /// </summary>
        public static SimulationConfig SeparatorSplit()
        {
            var c = New(1102, 600);
            var src = El("orders", "Source");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Exponential");
            src.Set("interArrivalDistribution.exponentialRate", 0.2);   // mean 5
            Add(c, src);
            var sep = El("split", "Separator");
            sep.Settings = new SeparatorSettings
            {
                Mode = SeparatorMode.Split, Capacity = 2, ProcessTime = SamplerSettings.Constant(2), SplitQuantity = SamplerSettings.Constant(3),
            };
            Add(c, sep);
            Add(c, Queue("buffer", 6));
            Add(c, Sink("sink"));
            Connect(c, "orders", "split"); Connect(c, "split", "buffer"); Connect(c, "buffer", "sink");
            return c;
        }

        /// <summary>
        /// A pick face of SKU P (kanban of one load of 3 pieces) pulls its loads from the published stock (instant
        /// moves); a station (2 per piece) takes single pieces; emptied loads leave through port <c>empties</c>. The
        /// replenisher (Warehousing pack) orders 3 loads once 3 have been consumed (lead time 20).
        /// </summary>
        public static SimulationConfig PickFaceKanban()
        {
            var c = New(1031, 50);
            var repl = El("repl", "BaseStockReplenisher");
            var rs = new ReplenisherSettings { TruckloadSize = 3, LeadTime = SamplerSettings.Constant(20) };
            rs.Skus.Add(SkuRow("P", 3, 3));
            repl.Settings = rs;
            Add(c, repl);
            Add(c, StorageEl("wh", SimuLean.Storage.AccessDiscipline.Direct, ("L1", 0, 10, null)));
            var face = El("face", "PickFace");
            face.Settings = new PickFaceSettings { Sku = "P", PiecesPerLoad = 3 };
            Add(c, face);
            Add(c, Server("station", 1, "Constant", ("constantValue", 2.0)));
            Add(c, Sink("sink")); Add(c, Sink("empties"));
            Connect(c, "repl", "wh"); Connect(c, "face", "station"); Connect(c, "station", "sink");
            var port = new ConnectionConfig("face", "empties", "PortOutput");
            port.Port = "empties";
            c.Connections.Add(port);
            c.Lists.Add(new ListConfig { Id = "stock", Name = "stock" });
            Push(c, "wh", "stock");
            Pull(c, "stock", "face", "sku == P");
            return c;
        }

        /// <summary>
        /// Minutes (calendar unit 60 s, starting on Monday 2026-01-05 00:00): a line (60 per load) pulls everything from the
        /// published stock; the replenisher orders 2 loads per truck (lead time 120) and unloads only 06:00–14:00, each load
        /// carried by the forklift (abstract, load 2.5 + unload 2.5). The first truck arrives at 180 and waits for the
        /// window (360); its loads are stored at 365 and 370.
        /// </summary>
        public static SimulationConfig BaseStockTruck()
        {
            var c = New(1032, 600);
            c.StartDateTime = "2026-01-05 00:00";   // a Monday
            c.TimeUnitSeconds = 60;
            var repl = El("repl", "BaseStockReplenisher");
            var rs = new ReplenisherSettings
            {
                TruckloadSize = 2, LeadTime = SamplerSettings.Constant(120), UnloadingWindows = "Mon-Fri 06:00-14:00",
            };
            rs.Skus.Add(SkuRow("A", 1, 2)); rs.Skus.Add(SkuRow("B", 1, 1));
            repl.Settings = rs;
            repl.Set("transportBy.team", "forklift");
            repl.Set("transportBy.loadTime", "Constant~2.5");
            repl.Set("transportBy.unloadTime", "Constant~2.5");
            Add(c, repl);
            Add(c, StorageEl("wh", SimuLean.Storage.AccessDiscipline.Direct, ("L1", 0, 10, null)));
            Add(c, Server("line", 1, "Constant", ("constantValue", 60.0)));
            Add(c, Sink("sink"));
            Connect(c, "repl", "wh"); Connect(c, "line", "sink");
            Team(c, "forklift", "", "", ("F1", new string[0]));
            c.Lists.Add(new ListConfig { Id = "stock", Name = "stock" });
            Push(c, "wh", "stock");
            Pull(c, "stock", "line", "");
            return c;
        }

        private static ReplenisherSkuSettings SkuRow(string sku, int pieces, int initial)
            => new ReplenisherSkuSettings { Sku = sku, PiecesPerLoad = pieces, InitialLoads = initial };

        /// <summary>A storage of lanes (x, capacity, network node) with one access discipline, first available lane.</summary>
        private static ElementConfig StorageEl(string id, SimuLean.Storage.AccessDiscipline access, params (string lane, double x, int cap, string node)[] lanes)
        {
            var e = El(id, "StorageSystem");
            var records = new List<StorageLaneSettings>();
            foreach (var l in lanes)
                records.Add(StorageLaneSettings.Of(l.lane, l.x, 0, 0, l.cap, access, SimuLean.Storage.SkuConstraint.Mixed, l.node));
            e.SettingsAs<StorageSystemSettings>().Lanes = records;
            e.Set("locationPolicy", "FirstAvailable");
            return e;
        }

        /// <summary>Schedule source of single items at the given times, each with its label <c>sku</c>.</summary>
        private static ElementConfig SkuArrivals(string id, params (double time, string sku)[] rows)
        {
            var table = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string>(), ["Name"] = new List<string>(), ["Q"] = new List<string>(), ["sku"] = new List<string>(),
            };
            foreach (var r in rows)
            {
                table["Time"].Add(r.time.ToString(System.Globalization.CultureInfo.InvariantCulture));
                table["Name"].Add(r.sku);
                table["Q"].Add("1");
                table["sku"].Add(r.sku);
            }
            var e = El(id, "Source");
            e.Set("mode", "Schedule");
            e.Set("arrivalsTable", table);
            return e;
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
            mx.Entries.Add(TravelMatrixEntryConfig.Of("H", "P", 2));
            mx.Entries.Add(TravelMatrixEntryConfig.Of("H", "C", 3));
            mx.Entries.Add(TravelMatrixEntryConfig.Of("P", "C", 5));
            c.TravelMatrices.Add(mx);
            Add(c, ScheduleSource("blocks", 2, 1.0, ("kind", "block")));
            Add(c, Placed(Server("prep", 1, "Constant", ("constantValue", 2.0)), "P"));
            Add(c, ScheduleSource("kits", 4, 1.0, ("kind", "kit")));
            Add(c, Queue("rack", 5));
            var asm = El("assembly", "Combiner");
            asm.Set("requirements", new[] { 1 });
            asm.Set("delayStrategy.strategyType", "Constant");
            asm.Set("delayStrategy.constantValue", 4.0);
            asm.Set("inputOrder", new List<string> { "prep", "rack" });
            Add(c, Placed(asm, "C"));
            Add(c, Sink("erected"));
            Connect(c, "blocks", "prep"); Connect(c, "prep", "assembly"); Connect(c, "kits", "rack"); Connect(c, "rack", "assembly");
            Connect(c, "assembly", "erected");
            var t = new ResourceTeamConfig { Id = "welders", Name = "welders", Travel = TravelKind.Matrix, Matrix = "floor", Speed = 1.0, HomeNode = "H" };
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
            yard.Set("offerOrder", release);
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
        /// crane is the yard's transport (the holder's).</summary>
        public static SimulationConfig YardKittingList()
        {
            var c = New(1042, 200);
            Add(c, ScheduleSourceBy("blocksA", 2, 0.0, 0.0, i => ("block", i == 0 ? "3" : "1")));
            Add(c, ScheduleSourceBy("blocksB", 2, 0.0, 0.0, i => ("block", i == 0 ? "2" : "4")));
            Add(c, ScheduleSourceBy("subs", 4, 1.0, 0.0, i => ("block", (i + 1).ToString())));
            Add(c, Queue("yard", 4));
            var a = Bay("bayA", "blocksA", 1); a.Set("inputOrder", new List<string> { "blocksA", "" });
            var b = Bay("bayB", "blocksB", 1); b.Set("inputOrder", new List<string> { "blocksB", "" });
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
            var a = Bay("bayA", "blocksA", 1); a.Set("inputOrder", new List<string> { "blocksA", "" });
            var b = Bay("bayB", "blocksB", 1); b.Set("inputOrder", new List<string> { "blocksB", "" });
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

        /// <summary>
        /// A welding queue that pulls from list W and pushes back to it (test only, not a baseline): four nodes wait at a
        /// bed that pushes to W and whose crane carries what it hands over; the queue pulls the bed's nodes while the robot
        /// is busy, and the robot pulls any entry of W. A node the crane delivers to the queue must be listed in W again.
        /// </summary>
        public static SimulationConfig WeldQueueSameList()
        {
            var c = New(1045, 200);
            Add(c, ScheduleSourceBy("nodes", 4, 0.0, 0.0, i => ("order", (i + 1).ToString())));
            Add(c, Queue("bed", 4));
            Add(c, Queue("weldQueue", 10));
            Add(c, Server("robot", 1, "Constant", ("constantValue", 5.0)));
            Add(c, Sink("welded"));
            Connect(c, "nodes", "bed");
            Connect(c, "robot", "welded");
            c.Lists.Add(new ListConfig { Id = "W", Name = "W" });
            Push(c, "bed", "W"); Push(c, "weldQueue", "W");
            Pull(c, "W", "robot", "");
            Pull(c, "W", "weldQueue", "origin.name == bed");
            Crane(c, "bed");
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
            yard.Set("offerOrder", "AnyAccepted");
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
            var a = Bay("bayA", "blocksA", 1); a.Set("inputOrder", new List<string> { "blocksA", "" });
            var b = Bay("bayB", "blocksB", 1); b.Set("inputOrder", new List<string> { "blocksB", "" });
            Add(c, Placed(a, "A")); Add(c, Placed(b, "B"));
            Add(c, Sink("erectedA")); Add(c, Sink("erectedB"));
            Connect(c, "subs", "yard");
            Connect(c, "blocksA", "bayA"); Connect(c, "blocksB", "bayB");
            Connect(c, "bayA", "erectedA"); Connect(c, "bayB", "erectedB");
            c.Lists.Add(new ListConfig { Id = "yardList", Name = "yardList" });
            Push(c, "yard", "yardList");
            Pull(c, "yardList", "bayA", "", port: 1);
            Pull(c, "yardList", "bayB", "", port: 1);
            var t = new ResourceTeamConfig { Id = "crane", Name = "crane", Travel = TravelKind.Axes, HomeNode = "Y",
                                             AxisSpeed = new double[] { 2, 2, 1 }, AxisAccel = new double[] { 1, 1, 1 } };
            t.Members.Add(new ResourceMemberConfig { Name = "Crane1", LoadTime = 0.5, UnloadTime = 0.5 });
            c.ResourceTeams.Add(t);
            c.Elements.Find(x => x.Id == "yard").Set("transportBy.team", "crane");
            c.StopWhenEmpty = true;
            return c;
        }

        private static ElementConfig Bay(string id, string mainSource, int capacity)
        {
            var bay = El(id, "Combiner");
            bay.Set("requirements", new[] { 1 });
            bay.Set("capacity", capacity);
            bay.Set("pullModeType", "SingleLabel");
            bay.Set("singleLabelName", "block");
            bay.Set("singleLabelValue", "");
            bay.Set("delayStrategy.strategyType", "Constant");
            bay.Set("delayStrategy.constantValue", 5.0);
            bay.Set("inputOrder", new List<string> { mainSource, "yard" });
            return bay;
        }

        // The crane: one abstract carrier, load 0.5, travel 2, unload 0.5.
        private static void Crane(SimulationConfig c, string holder)
        {
            Team(c, "crane", "", "", ("Crane1", new string[0]));
            var e = c.Elements.Find(x => x.Id == holder);
            e.Set("transportBy.team", "crane");
            e.Set("transportBy.loadTime", "Constant~0.5");
            e.Set("transportBy.travelTime", "Constant~2");
            e.Set("transportBy.unloadTime", "Constant~0.5");
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
            var e = El(id, "Source");
            e.Set("mode", "InterArrival");
            e.Set("interArrivalDistribution.strategyType", "Exponential");
            e.Set("interArrivalDistribution.exponentialRate", 1.0 / meanGap);
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
            var e = El(id, "Source");
            e.Set("mode", "Schedule");
            e.Set("arrivalsTable", table);
            return e;
        }

        private static ElementConfig Queue(string id, int capacity)
        {
            var e = El(id, "Queue");
            e.Set("capacity", capacity);
            return e;
        }

        private static ElementConfig Server(string id, int capacity, string dist, params (string key, double value)[] p)
        {
            var e = El(id, "MultiServer");
            e.Set("capacity", capacity);
            e.Set("delayStrategy.strategyType", dist);
            foreach (var kv in p) e.Set("delayStrategy" + "." + kv.key, kv.value);
            return e;
        }

        private static ElementConfig Sink(string id) => El(id, "Sink");
    }
}
