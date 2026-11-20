using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Collections;
using SimuLean.Resources;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Builds the resource side of a <see cref="SimulationConfig"/>: teams (a <see cref="ResourceDispatcher"/> and its
    /// <see cref="TaskExecutor"/> members) and the process / transport resource use of elements
    /// (their <c>processBy</c> / <c>transportBy</c> settings). Used by the headless factory; the Unity wrappers build the same objects.
    /// </summary>
    /// <summary>Where things can be in a model: the network's nodes and the travel matrices' locations.</summary>
    public sealed class ModelSpace
    {
        public SimuLean.Transport.TransportNetwork Network { get; set; }
        public Dictionary<string, DistanceMatrix> Matrices { get; } = new Dictionary<string, DistanceMatrix>();
        public Dictionary<string, WalkGrid> Grids { get; } = new Dictionary<string, WalkGrid>();
        /// <summary>Traffic on the network (shared by every team with <c>Traffic</c>).</summary>
        public TrafficControl Traffic { get; set; }

        public bool IsLocation(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (Network != null && Network.GetNode(id) != null) return true;
            foreach (var m in Matrices.Values) if (m.Locations.Contains(id)) return true;
            return false;
        }
    }

    /// <summary>Units of a team's speeds, accelerations and load / unload times (<see cref="ResourceTeamConfig.Units"/>).</summary>
    public enum ResourceUnits
    {
        /// <summary>Model units: distance per model time unit, model time units (the clock's unit).</summary>
        Model,
        /// <summary>Physical units: distance per second (m/s), distance per second² and seconds, whatever the clock's unit.</summary>
        Physical,
    }

    public static class ResourceBuild
    {
        /// <summary>The team's config in model units: with <see cref="ResourceUnits.Physical"/>, speeds × seconds per unit,
        /// accelerations × its square, load / unload times ÷ it. A copy; the config is not changed. A travel
        /// calibration is measured in model time already and is left as it is.</summary>
        public static ResourceTeamConfig InModelUnits(ResourceTeamConfig tc, double secondsPerUnit)
        {
            if (!IsPhysical(tc) || secondsPerUnit == 1.0) return tc;
            if (!(secondsPerUnit > 0)) throw new ArgumentOutOfRangeException(nameof(secondsPerUnit));
            var c = ConfigCloner.DeepClone(tc);
            double u = secondsPerUnit;
            c.Speed = tc.Speed * u;
            var vs = Triple(tc.AxisSpeed, tc.Id); var acc = Triple(tc.AxisAccel, tc.Id);
            c.AxisSpeed = new[] { vs[0] * u, vs[1] * u, vs[2] * u };
            c.AxisAccel = new[] { acc[0] * u * u, acc[1] * u * u, acc[2] * u * u };
            foreach (var m in c.Members) { m.LoadTime /= u; m.UnloadTime /= u; }
            return c;
        }

        /// <summary>True when the team's values are in physical units.</summary>
        /// <summary>The team's name as the built team has it (its id when unnamed).</summary>
        public static string TeamName(ResourceTeamConfig tc) => string.IsNullOrEmpty(tc?.Name) ? tc?.Id : tc.Name;

        /// <summary>The name of member <paramref name="number"/> (1-based) of a team: its own, else <c>&lt;Team&gt;_&lt;n&gt;</c> — the
        /// name its statistics and a downtime on it (<c>downtime:Id@&lt;member&gt;</c>) carry.</summary>
        public static string MemberName(string teamName, ResourceMemberConfig m, int number)
            => string.IsNullOrEmpty(m?.Name) ? teamName + "_" + number : m.Name;

        public static bool IsPhysical(ResourceTeamConfig tc) => tc != null && tc.Units == ResourceUnits.Physical;

        /// <summary>A team from its config; its members are appended to <paramref name="executors"/>. Spatial
        /// travel needs the model's locations (<paramref name="space"/>: network, matrices).</summary>
        public static ResourceDispatcher BuildTeam(ResourceTeamConfig tc, SimClock clock, List<TaskExecutor> executors,
                                                   ModelSpace space = null)
        {
            space = space ?? new ModelSpace();
            tc = InModelUnits(tc, clock.Context.Calendar.SecondsPerUnit);
            ITravelModel travel = TravelOf(tc, space);
            bool onNetwork = travel != null;
            // no home node: the network's first node (a Network team without one failed the build
            // with "home '' is not a location"); simulean_validate says which node it is (Study.Check, info)
            string home = string.IsNullOrEmpty(tc.HomeNode) && onNetwork ? DefaultHome(space) : tc.HomeNode;
            if (onNetwork && (string.IsNullOrEmpty(home) || !space.IsLocation(home)))
                throw new ArgumentException("Resource team '" + (tc.Name ?? tc.Id) + "': " + (string.IsNullOrEmpty(home)
                    ? "it travels spatially but the model has no node to start at: set its homeNode to a Transport Node of the network."
                    : "home '" + home + "' is not a location of the model: set its homeNode to a Transport Node of the network."));
            string requestOrder = NamedOrder(tc);
            string requestRule = SimuLean.Rules.ChoiceRules.OrderRule(requestOrder);
            var d = new ResourceDispatcher(TeamName(tc), clock)
            {
                RequestOrder = requestRule != null ? null : OrderOf(requestOrder, ListFields.Request, ListFields.RequestRoots, $"Resource team '{tc.Id}', request order"),
            };
            if (requestRule != null)
            {
                // a dispatch rule: it scores the pending requests
                string owner = "Resource team '" + d.Name + "'";
                var rule = (SimuLean.Rules.IDispatchRule)SimuLean.Rules.ChoiceRules.Create(requestRule, SimuLean.Rules.RuleSite.Dispatch, tc.Rule, new[] { requestRule }, owner);
                d.RequestRule = new SimuLean.Rules.RuleRequestOrder(rule, d.Name);
            }
            else if (tc.Rule != null && tc.Rule.Count > 0)
                throw new ArgumentException($"Resource team '{tc.Name ?? tc.Id}': rule parameters ({string.Join(", ", tc.Rule.ConvertAll(r => r?.Name))}) but no dispatch rule (requestOrder = rule:Type).");
            int i = 0;
            foreach (var m in tc.Members)
            {
                i++;
                var ex = new TaskExecutor(MemberName(d.Name, m, i), clock,
                                          onNetwork ? ResourceMode.Spatial : ResourceMode.Abstract)
                {
                    LoadTime = m.LoadTime, UnloadTime = m.UnloadTime,
                    HomeNode = onNetwork ? home : null, TravelModel = travel, ReturnHome = onNetwork && tc.ReturnHome,
                    RecordMoves = travel is ExternalTravel,   // moves timed outside: the calibration's input
                    OnStop = StopPolicyOf(tc),
                };
                // name = expression, evaluated once now (the one label syntax: skill = 'weld', level = 3)
                LabelAssignments.Parse(m.Properties, $"Resource '{ex.Name}' of team '{d.Name}'", "property", ownerIsDescribed: true)?.ApplyTo(ex);
                d.Add(ex);
                executors.Add(ex);
            }
            clock.Context.RegisterTeam(d.Name, d);
            clock.Context.RegisterTeam(tc.Id, d);
            // a member order reads the members' fields and properties: known once they are built
            d.ResourceOrder = OrderOf(tc.ResourceOrder, MemberFieldNames(d), ListFields.MemberRoots, $"Resource team '{tc.Id}', resource order");
            return d;
        }

        /// <summary>
        /// The home of a spatial team without <see cref="ResourceTeamConfig.HomeNode"/>: the network's first node — a node of
        /// the modeller's (its id has no <c>:</c>; the scene's automatic points <c>pos:</c> / <c>home:</c> / <c>work:</c> are
        /// skipped), else the first of all; null without a network (a matrix's locations have no order: its team needs one).
        /// </summary>
        public static string DefaultHome(ModelSpace space)
        {
            if (space?.Network != null)
            {
                string first = null;
                foreach (var n in space.Network.Nodes)
                {
                    if (first == null) first = n.Id;
                    if (n.Id != null && n.Id.IndexOf(':') < 0) return n.Id;
                }
                if (first != null) return first;
            }
            return null;
        }

        /// <summary>The team's <see cref="ResourceTeamConfig.JobOnMemberStop"/> (no team = hand over).</summary>
        public static ResourceStopPolicy StopPolicyOf(ResourceTeamConfig tc) => tc?.JobOnMemberStop ?? ResourceStopPolicy.HandOver;


        /// <summary>The repair crew of a downtime's generator on <paramref name="target"/> (null when the downtime has no
        /// <c>repairBy.team</c>): team, requirements, priority (an expression over the element), mode and preemptable — a
        /// repair is not preemptable unless <c>repairBy.preemptable</c> says so.</summary>
        public static StationResources RepairUse(DowntimeConfig cfg, IReadOnlyDictionary<string, ResourceDispatcher> teams, Element target)
        {
            string owner = "Downtime '" + cfg.Id + "'";
            teams = teams ?? new Dictionary<string, ResourceDispatcher>();
            var r = cfg.Settings?.RepairBy;
            if (r == null) return null;
            var use = Use(owner, r.Team ?? "", Requirements(r.Requirements, teams, owner), r.Priority ?? "",
                          "priority:repairBy.priority", teams, target, r.Preemption, r.Preemptable, r.Acquire);
            // the crew's max wait: the stream of each downtime's limit is its own (several downtimes on one target)
            ApplyMaxWait(use, r.MaxWait ?? "", r.OnTimeout ?? "", owner, "repairBy.onTimeout", "repairBy.maxWait:" + cfg.Id, target);
            return use;
        }

        /// <summary>How the items of an element walk by themselves (<c>walkBy.speed</c> &gt; 0), or null: the resources'
        /// travel model (network — the default —, walk grid or matrix) at the walking speed, given in m/s and converted to
        /// model units as a physical team's speed.</summary>
        public static ITravelModel WalkTravel(ElementConfig ec, ModelSpace space, double secondsPerUnit)
        {
            var w = ec.Settings?.WalkBy;
            double speed = w?.Speed ?? 0.0;
            if (!(speed > 0)) return null;
            string route = w.Route ?? "";
            route = string.IsNullOrWhiteSpace(route) ? "Network" : route.Trim();
            TravelKind kind;
            if (route.Equals("Network", StringComparison.OrdinalIgnoreCase)) kind = TravelKind.Network;
            else if (route.Equals("Grid", StringComparison.OrdinalIgnoreCase)) kind = TravelKind.Grid;
            else if (route.Equals("Matrix", StringComparison.OrdinalIgnoreCase)) kind = TravelKind.Matrix;
            else throw new ArgumentException($"Element '{ec.Name}': items walk on the Network, a Grid or a Matrix, not '{route}'.");
            var tc = new ResourceTeamConfig
            {
                Id = ec.Name, Travel = kind, Speed = speed, Units = ResourceUnits.Physical,
                WalkGrid = w.Grid ?? "", Matrix = w.Matrix ?? "",
            };
            return TravelOf(InModelUnits(tc, secondsPerUnit), space ?? new ModelSpace());
        }

        // The team's travel model (null = abstract).
        private static ITravelModel TravelOf(ResourceTeamConfig tc, ModelSpace space) => TravelOf(tc, space, tc.Travel);

        private static ITravelModel TravelOf(ResourceTeamConfig tc, ModelSpace space, TravelKind kind)
        {
            switch (kind)
            {
                case TravelKind.Abstract: return null;
                case TravelKind.External:
                {
                    // Moves timed outside the engine (Unity physics in a scene run): the estimate model gives distances.
                    var baseKind = tc.CalibrationBase;
                    if (baseKind == null || baseKind == TravelKind.Abstract || baseKind == TravelKind.External || baseKind == TravelKind.Calibrated)
                        throw new ArgumentException("Resource team '" + tc.Id + "': external travel needs an estimate model (Network, Matrix or Grid).");
                    return new ExternalTravel(TravelOf(tc, space, baseKind.Value));
                }
                case TravelKind.Calibrated:
                {
                    var baseKind = tc.CalibrationBase;
                    if (baseKind == null || baseKind == TravelKind.Abstract || baseKind == TravelKind.Calibrated)
                        throw new ArgumentException("Resource team '" + tc.Id + "': calibrated travel needs an estimate model (Network, Matrix or Grid).");
                    var estimate = TravelOf(tc, space, baseKind.Value);
                    var abc = Triple(tc.CalibrationFit, tc.Id);
                    var cal = new CalibratedTravel(estimate, abc[0], abc[1], abc[2], tc.CalibrationMinSamples);
                    foreach (var pair in tc.CalibrationPairs ?? new List<CalibrationPairSettings>())
                        if (pair != null) cal.AddPair(pair.ToStat());
                    return cal;
                }
                case TravelKind.Network:
                {
                    if (space.Network == null)
                        throw new ArgumentException("Resource team '" + tc.Id + "' travels on the network, but the model has none.");
                    var nt = new NetworkTravel(space.Network, tc.Speed);
                    if (!tc.Traffic) return nt;
                    if (space.Traffic == null) space.Traffic = new TrafficControl();
                    return new TrafficNetworkTravel(nt, space.Traffic);
                }
                case TravelKind.Matrix:
                    if (tc.Matrix == null || !space.Matrices.TryGetValue(tc.Matrix, out DistanceMatrix m))
                        throw new ArgumentException("Resource team '" + tc.Id + "': travel matrix '" + tc.Matrix + "' does not exist.");
                    return new MatrixTravel(m, tc.Speed);
                case TravelKind.Grid:
                    if (tc.WalkGrid == null || !space.Grids.TryGetValue(tc.WalkGrid, out WalkGrid grid))
                        throw new ArgumentException("Resource team '" + tc.Id + "': walk grid '" + tc.WalkGrid + "' does not exist.");
                    if (space.Network == null)
                        throw new ArgumentException("Resource team '" + tc.Id + "' walks between points, but the model has none.");
                    return new GridTravel(grid, space.Network, tc.Speed);
                case TravelKind.Straight:
                    if (space.Network == null)
                        throw new ArgumentException("Resource team '" + tc.Id + "' travels in straight lines between points, but the model has none.");
                    return new StraightTravel(space.Network, tc.Speed);
                case TravelKind.Axes:
                    if (space.Network == null)
                        throw new ArgumentException("Resource team '" + tc.Id + "' travels by axes between points, but the model has none.");
                    return new AxisTravel(space.Network, Triple(tc.AxisSpeed, tc.Id), Triple(tc.AxisAccel, tc.Id), tc.AxesSequential);
            }
            throw new ArgumentException("Resource team '" + tc.Id + "': unknown travel '" + kind + "'.");
        }

        // x, y, z (missing values are 0; more than three fail)
        private static double[] Triple(double[] values, string team)
        {
            var v = new double[3];
            if (values == null) return v;
            if (values.Length > 3) throw new FormatException("Resource team '" + team + "': " + values.Length + " axis values (expected x, y, z).");
            Array.Copy(values, v, values.Length);
            return v;
        }

        /// <summary>A matrix from its config (typed entries).</summary>
        public static DistanceMatrix BuildMatrix(TravelMatrixConfig mc)
        {
            var m = new DistanceMatrix(mc.Symmetric);
            foreach (var e in mc.Entries ?? new List<TravelMatrixEntryConfig>())
            {
                if (e == null) continue;
                if (string.IsNullOrWhiteSpace(e.From) || string.IsNullOrWhiteSpace(e.To))
                    throw new FormatException("Travel matrix '" + mc.Id + "': an entry without both locations.");
                m.Set(e.From.Trim(), e.To.Trim(), e.Distance);
            }
            return m;
        }

        /// <summary>A request priority given by an expression over the item and the element (a bare name = the item's
        /// label, e.g. <c>prio</c>; or <c>item.prio * 10 + item.age</c>).</summary>
        public static Func<Item, double> PriorityOf(string expression, Element element, string purpose = "priority")
        {
            var ex = SimuLean.Expressions.Expression.Parse(expression, SimuLean.Expressions.ExpressionContext.Priority, element?.Name, purpose);
            var scope = new SimuLean.Expressions.EntityScope();
            // its own stream (CLAUDE.md: one stream per use): it drew from the element's fallback <element>/expr
            return item =>
            {
                scope.RandomStream = element?.GetSimClock() != null ? element.RandomStreamFor(purpose) : null;
                return ex.Number(scope.Set(item, element));
            };
        }

        public static WalkGrid BuildGrid(WalkGridConfig gc)
            => WalkGrid.FromRows(gc.OriginX, gc.OriginZ, gc.CellSize, gc.Rows, gc.Y);

        /// <summary>A team's request order as a query text: one of its named orders by name, else the text written in
        /// place. A plain name that is none of the named orders fails, naming them.</summary>
        public static string NamedOrder(ResourceTeamConfig tc)
        {
            string text = (tc.RequestOrder ?? "").Trim();
            if (text.Length == 0) return text;
            var names = new List<string>();
            foreach (var o in tc.NamedOrders ?? new List<NamedOrderSettings>())
            {
                if (o == null || string.IsNullOrWhiteSpace(o.Name)) continue;
                if (o.Name.Trim() == text) return (o.Query ?? "").Trim();
                names.Add(o.Name.Trim());
            }
            if (names.Count > 0 && text.IndexOf('|') < 0 && !text.Contains(" ") && SimuLean.Rules.ChoiceRules.OrderRule(text) == null)
                throw new ArgumentException($"Resource team '{tc.Name ?? tc.Id}': request order '{text}' is not one of its named orders " +
                                            $"({string.Join(", ", names)}) nor a query ('|priority desc')." + NameSuggestions.DidYouMean(text, names));
            return text;
        }

        // an order key / query over a team's requests or members: its fields are checked now
        private static ListQuery OrderOf(string spec, IEnumerable<string> known, IReadOnlyList<string> roots, string where)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var q = ListQuerySpec.Decode(spec);
            ListFields.Check(q, spec, known, roots, false, where);
            return q.ToQuery();
        }

        /// <summary>What a query over the members of <paramref name="d"/> may read: their fields and properties.</summary>
        public static List<string> MemberFieldNames(ResourceDispatcher d)
        {
            var names = new List<string>(ListFields.Member);
            foreach (var m in d.Members)
                if (m.HasProperties) foreach (string p in m.Properties.Names) if (!names.Contains(p)) names.Add(p);
            return names;
        }

        /// <summary>The element's process resources (its <c>processBy</c>; null when it has no process team).</summary>
        public static StationResources ProcessUse(ElementConfig ec, IReadOnlyDictionary<string, ResourceDispatcher> teams, Element element = null,
                                                  SimClock clock = null)
        {
            var p = ec.Settings?.ProcessBy;
            if (p == null) return null;
            string owner = "Element '" + ec.Name + "'";
            var use = Use(owner, p.Team, Requirements(p.Requirements, teams, owner), p.Priority, "priority:processBy.priority", teams,
                          element, p.Preemption, p.Preemptable, p.Acquire);
            if (use != null)
            {
                use.Release = p.Release;
                use.HoldFor = Sampler(p.HoldFor, ec.Name, "processHoldFor", clock);
                use.AcquireBeforeEntry = p.AcquireBeforeEntry;
            }
            ApplyMaxWait(use, p.MaxWait, p.OnTimeout, owner, "processBy.onTimeout", "processBy.maxWait", element, rerouteAllowed: true);
            return use;
        }

        /// <summary>
        /// A use's max wait: <paramref name="maxWait"/> (a number or an expression; empty / ≤ 0 = none) and
        /// <paramref name="onTimeout"/> (<see cref="TimeoutAction"/>, named <paramref name="onTimeoutKey"/> in errors). The
        /// element (the station, or a downtime's target) then reports its <c>timeouts</c>. Reroute only for a process use (the
        /// Multi-Server checks its target at the build). <paramref name="purpose"/> keys the limit's random stream.
        /// </summary>
        private static void ApplyMaxWait(StationResources use, string maxWait, string onTimeout, string owner,
                                         string onTimeoutKey, string purpose, Element element, bool rerouteAllowed = false)
        {
            var action = WaitLimit.ActionOf(onTimeout ?? "", owner);
            var limit = WaitLimit.Parse(maxWait ?? "", action, owner, purpose);
            if (limit == null || use == null) return;
            if (action == TimeoutAction.Reroute && !rerouteAllowed)
                throw new FormatException($"{owner}: {onTimeoutKey} = Reroute is for a station's process (its item leaves by the " +
                                          "timeout target); a crew's wait can KeepWaiting or GiveUp.");
            use.MaxWait = limit;
            element?.DeclareWaitLimit(WaitKind.Resource);
        }

        /// <summary>
        /// The station's setup (its <c>setup</c>; null when absent or <c>setup.when</c> = None, so models without one are built
        /// exactly as without it). The setup time gets its own random stream (owner = the element, purpose <c>setup</c>): the
        /// process draws are unchanged. The crew is a use of a team as the
        /// process's (requests and tasks of type Setup). <c>setup.keepForProcess</c> applies only when the setup team is the process
        /// team: then no crew of its own is built — the process's resources are asked once, set up and process; otherwise it
        /// is ignored with a warning. An unknown rule, a LabelChange without a label or an element type that does not set up
        /// fail the build.
        /// </summary>
        public static StationSetup SetupOf(ElementConfig ec, IReadOnlyDictionary<string, ResourceDispatcher> teams, Element element,
                                           SimClock clock = null)
        {
            var s = ec.Settings?.Setup;
            if (s == null || s.When == SetupWhen.None) return null;
            SetupWhen w = s.When;
            if (element != null && !(element is ISetupStation))
                throw new FormatException($"Element '{ec.Name}' has a setup (setup.when = {w}), but its type does not set up " +
                                          "(Multi-Server, Combiner, Picking Station, Assembly Station, Stacker and Erection Dock do).");
            string label = (s.Label ?? "").Trim();
            if (w == SetupWhen.LabelChange && label.Length == 0)
                throw new FormatException($"'{ec.Name}': a setup on label change needs the label (setup.label).");
            var setup = new StationSetup { When = w, Label = label, First = s.SetupFirst };
            setup.Time = Sampler(s.Time, ec.Name, "setup", clock);
            string matrix = (s.Matrix ?? "").Trim();
            if (matrix.Length > 0)
            {
                // a time per from → to pair, from a data table of the model (its snapshot); each cell its own stream
                if (clock == null || !clock.Context.Data.TryGet(matrix, out var table))
                    throw new FormatException($"'{ec.Name}': no data table '{matrix}' in the model for its setup matrix " +
                                              $"(tables: {(clock == null || clock.Context.Data.Count == 0 ? "none" : string.Join(", ", clock.Context.Data.Names))}).");
                setup.Matrix = SetupMatrix.From(table, ec.Name, (cell, purpose) => SamplerSpec.Decode(cell, clock, ec.Name, purpose));
            }

            var crew = s.SetupBy;
            string setupTeam = (crew?.Team ?? "").Trim();
            if (s.KeepForProcess && element != null && !StationSetup.CanKeepForProcess(element))
                SimLog.Warning($"[SimuLean] '{ec.Name}': its type asks its process resources per task / cycle, so they cannot stay " +
                               "from the setup to the process ('keep the setup's resources for the process' ignored).");
            else if (s.KeepForProcess)
            {
                string processTeam = (ec.Settings.ProcessBy?.Team ?? "").Trim();
                if (setupTeam.Length > 0 && setupTeam == processTeam)
                {
                    setup.KeepForProcess = true;   // the process's resources do it: no crew of its own
                    return setup;
                }
                SimLog.Warning($"[SimuLean] '{ec.Name}': 'keep the setup's resources for the process' needs the setup and the process " +
                               "done by the same team; ignored.");
            }
            if (crew == null) return setup;
            string crewOwner = "Element '" + ec.Name + "' (setup)";
            setup.Resources = Use(crewOwner, crew.Team, Requirements(crew.Requirements, teams, crewOwner), crew.Priority,
                                  "priority:setup.setupBy.priority", teams, element, crew.Preemption, crew.Preemptable, crew.Acquire);
            if (setup.Resources != null) setup.Resources.Task = TaskType.Setup;
            ApplyMaxWait(setup.Resources, crew.MaxWait, crew.OnTimeout, crewOwner, "setup.setupBy.onTimeout", "setup.setupBy.maxWait", element);
            return setup;
        }

        /// <summary>
        /// The own resources of an assembly station's tasks (each task's <c>tasks[i].requirements</c>, records
        /// <c>count#query#label#team</c>): each task with requirements gets a use of its own — its requirements, the
        /// station's process use for the rest (its team as the default team, priority, preemption, release, acquisition
        /// order, own time). A task without a team of its own needs the station's process team.
        /// </summary>
        public static void AssemblyTaskUses(ElementConfig ec, IReadOnlyDictionary<string, ResourceDispatcher> teams,
                                            AssemblyStation station, StationResources stationUse)
        {
            var tasks = ec.SettingsAs<AssemblyStationSettings>()?.Tasks;
            if (tasks == null || station == null) return;
            string owner = "Assembly station '" + ec.Name + "'";
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                if (t == null || string.IsNullOrWhiteSpace(t.Id) || t.ProcessBy == null || !t.ProcessBy.Any) continue;
                string id = t.Id.Trim();
                string taskOwner = owner + ", task '" + id + "'";
                AssemblyTaskSpec spec = null;
                foreach (var x in station.Tasks) if (x.Id == id) { spec = x; break; }
                if (spec == null) continue;
                var own = t.ProcessBy;
                var reqs = Requirements(own.Requirements, teams, taskOwner);
                ResourceDispatcher team = null;
                if (!string.IsNullOrWhiteSpace(own.Team) && !teams.TryGetValue(own.Team.Trim(), out team))
                    throw new ArgumentException(taskOwner + ": resource team '" + own.Team + "' does not exist.");
                team = team ?? stationUse?.Dispatcher ?? (reqs.Count > 0 ? reqs[0].Team : null);
                if (team == null)
                    throw new FormatException(owner + ": task '" + id + "' names no team and the station has no process team.");
                if (reqs.Count == 0 && stationUse != null && team == stationUse.Dispatcher) reqs.AddRange(stationUse.Requirements);
                var use = new StationResources(team, reqs.ToArray());
                // the station's process use, then the task's own settings over it
                if (stationUse != null)
                {
                    use.Priority = stationUse.Priority; use.Preempt = stationUse.Preempt; use.Preemptable = stationUse.Preemptable;
                    use.Release = stationUse.Release; use.Order = stationUse.Order; use.HoldFor = stationUse.HoldFor;
                    use.ReleaseAt = stationUse.ReleaseAt; use.MaxWait = stationUse.MaxWait;
                }
                else
                {
                    // no station team: the tasks' uses still take the Process block's settings when given
                    var p = ec.Settings.ProcessBy ?? new ProcessUseSettings();
                    if (!string.IsNullOrWhiteSpace(p.Priority)) use.Priority = PriorityOf(p.Priority, station, "priority:task:" + id);
                    use.Preempt = p.Preemption;
                    use.Preemptable = p.Preemptable;
                    use.Order = p.Acquire;
                    ApplyMaxWait(use, p.MaxWait, p.OnTimeout, owner, "processBy.onTimeout", "processBy.maxWait", station);
                }
                if (!string.IsNullOrWhiteSpace(own.Priority)) use.Priority = PriorityOf(own.Priority, station, "priority:task:" + id);
                if (own.Preemption != null) use.Preempt = own.Preemption.Value;
                if (own.Preemptable != null) use.Preemptable = own.Preemptable.Value;
                if (own.Acquire != null) use.Order = own.Acquire.Value;
                if (own.MaxWait != null || own.OnTimeout != null)
                {
                    // a task's own max wait and action; the one it does not give is the station's
                    var p = ec.Settings.ProcessBy;
                    use.MaxWait = null;
                    ApplyMaxWait(use, own.MaxWait ?? p?.MaxWait, own.OnTimeout ?? p?.OnTimeout, taskOwner,
                                 "tasks[" + i.ToString(CultureInfo.InvariantCulture) + "].processBy.onTimeout", "maxWait:task:" + id, station);
                }
                spec.Resources = use;
            }
        }

        /// <summary>The element's transport resources (its <c>transportBy</c>; null when its items are not carried by resources).</summary>
        public static StationResources TransportUse(ElementConfig ec, IReadOnlyDictionary<string, ResourceDispatcher> teams, Element element = null)
        {
            var t = ec.Settings?.TransportBy;
            if (t == null) return null;
            string owner = "Element '" + ec.Name + "'";
            return Use(owner, t.Team, Requirements(t.Requirements, teams, owner), t.Priority, "priority:transportBy.priority", teams,
                       element, t.Preemption, t.Preemptable, acquire: null);
        }

        // the move's samplers of an element's transport use (one stream each; the purposes keep the names of the sampler keys they replace)
        private static (IRandomSampler load, IRandomSampler travel, IRandomSampler unload) MoveTimes(ElementConfig ec, SimClock clock)
        {
            var t = ec.Settings?.TransportBy;
            if (t == null) return (null, null, null);
            return (Sampler(t.LoadTime, ec.Name, "transportLoad", clock), Sampler(t.TravelTime, ec.Name, "transportTravel", clock),
                    Sampler(t.UnloadTime, ec.Name, "transportUnload", clock));
        }

        /// <summary>Sets the move times of a transport link from the element's sampler specs (one stream each).</summary>
        public static void ApplyTransportTimes(ResourceTransportLink link, ElementConfig ec, SimClock clock)
        {
            var (load, travel, unload) = MoveTimes(ec, clock);
            var origin = link.Origin;
            if (load != null) link.LoadTime = item => SimuLean.Expressions.Samplers.Sample(load, item, origin);
            if (unload != null) link.UnloadTime = item => SimuLean.Expressions.Samplers.Sample(unload, item, origin);
            if (travel != null) link.TravelTime = (item, dest) => SimuLean.Expressions.Samplers.Sample(travel, item, origin);
        }

        /// <summary>The same for the moves of a list holder.</summary>
        public static void ApplyTransportTimes(TransportMoves link, ElementConfig ec, SimClock clock)
        {
            var (load, travel, unload) = MoveTimes(ec, clock);
            var origin = link.Origin;
            if (load != null) link.LoadTime = item => SimuLean.Expressions.Samplers.Sample(load, item, origin);
            if (unload != null) link.UnloadTime = item => SimuLean.Expressions.Samplers.Sample(unload, item, origin);
            if (travel != null) link.TravelTime = (item, dest) => SimuLean.Expressions.Samplers.Sample(travel, item, origin);
        }

        /// <summary>The load / unload times of a storage's moves from its transport load / unload samplers (one stream each).</summary>
        public static void ApplyStorageMoveTimes(SimuLean.Storage.StorageSystem storage, ElementConfig ec, SimClock clock)
        {
            var (load, _, unload) = MoveTimes(ec, clock);
            storage.MoveLoadTime = load != null ? item => SimuLean.Expressions.Samplers.Sample(load, item, storage) : (Func<Item, double>)null;
            storage.MoveUnloadTime = unload != null ? item => SimuLean.Expressions.Samplers.Sample(unload, item, storage) : (Func<Item, double>)null;
        }

        // a distribution (null = none) with its own stream: owner = the element, purpose as given
        private static IRandomSampler Sampler(SamplerSettings s, string owner, string purpose, SimClock clock)
            => ElementBuildHelpers.SamplerOf(s, clock, owner, purpose, purpose);

        // One use of a team (an element's process / transport, a setup crew, a downtime's repair crew): its team (empty = no
        // use), requirements (already decoded, checked after the team), priority (an expression; its stream keyed by
        // priorityPurpose), preemption and acquisition order. acquire null = a transport use: its requirements must all be of
        // its own team (a joint move gathers one team).
        //
        // The use's team is a switch and the default team. Empty, with requirements that name their own teams,
        // the use is active (its first requirement's team stands for it); a requirement without a team then has none to
        // take its members from — a build error that says what to set (it was silently ignored: no resources at all).
        private static StationResources Use(string owner, string team, List<ResourceRequirement> reqs, string priority, string priorityPurpose,
                                            IReadOnlyDictionary<string, ResourceDispatcher> teams, Element element,
                                            PreemptMode preempt, bool preemptable, AcquireOrder? acquire)
        {
            ResourceDispatcher d;
            if (string.IsNullOrWhiteSpace(team))
            {
                if (reqs == null || reqs.Count == 0) return null;
                string group = UseGroup(priorityPurpose);
                int missing = reqs.FindIndex(r => r.Team == null);
                if (missing >= 0)
                    throw new FormatException(owner + ": " + group + ".requirements[" + missing.ToString(CultureInfo.InvariantCulture) +
                                              "] names no team and " + group + ".team is empty: set " + group + ".team (the team of every " +
                                              "requirement without one) or the requirement's team.");
                d = reqs[0].Team;
            }
            else if (!teams.TryGetValue(team, out d))
                throw new ArgumentException(owner + ": resource team '" + team + "' does not exist.");
            if (acquire == null)
                foreach (var r in reqs)
                    if (r.Team != null && r.Team != d)
                        throw new FormatException(owner + ": a transport's requirements are all of its own team (a move of several " +
                                                  "carriers gathers members of one team); requirement team '" + r.Team.Name + "' is not '" + d.Name + "'.");
            var use = new StationResources(d, reqs.ToArray());
            if (acquire != null) use.Order = acquire.Value;
            if (!string.IsNullOrWhiteSpace(priority)) use.Priority = PriorityOf(priority, element, priorityPurpose);
            use.Preempt = preempt;
            use.Preemptable = preemptable;
            return use;
        }

        // "priority:processBy.priority" → "processBy" (the use's key group, for messages)
        private static string UseGroup(string priorityPurpose)
        {
            string g = priorityPurpose ?? "";
            if (g.StartsWith("priority:", StringComparison.Ordinal)) g = g.Substring("priority:".Length);
            if (g.EndsWith(".priority", StringComparison.Ordinal)) g = g.Substring(0, g.Length - ".priority".Length);
            return g;
        }

        /// <summary>True when a use is active: its team is set, or one of its requirements names a team.</summary>
        public static bool IsActive(string team, List<RequirementSettings> requirements)
            => !string.IsNullOrWhiteSpace(team) || (requirements != null && requirements.Exists(r => r != null && !string.IsNullOrWhiteSpace(r.Team)));

        /// <summary><c>count#query#label</c> (query = <see cref="ListQuerySpec"/> text).</summary>
        public static string EncodeRequirement(int count, string query, string label)
            => count.ToString(CultureInfo.InvariantCulture) + "#" + (query ?? "") + "#" + (label ?? "");

        /// <summary><c>count#query#label#team</c>: a requirement of another team (its id; empty = the use's team).</summary>
        public static string EncodeRequirement(int count, string query, string label, string team)
            => string.IsNullOrEmpty(team) ? EncodeRequirement(count, query, label) : EncodeRequirement(count, query, label) + "#" + team;

        /// <summary>The acquisition order of a use (empty = AllAtOnce); an unknown one fails the build naming the owner.</summary>
        public static AcquireOrder AcquireOf(IConfigValues parameters, string owner, string key)
        {
            string s = parameters.Get(key, "");
            if (string.IsNullOrWhiteSpace(s)) return AcquireOrder.AllAtOnce;
            if (!Enum.TryParse(s.Trim(), true, out AcquireOrder o) || !Enum.IsDefined(typeof(AcquireOrder), o))
                throw new FormatException($"{owner}: unknown acquisition order '{s}' (AllAtOnce or InOrder).");
            return o;
        }

        /// <summary>A requirement record with its team resolved among <paramref name="teams"/> (a fourth field; an unknown
        /// team fails the build naming <paramref name="owner"/>).</summary>
        /// <summary>An element use's typed requirements (<see cref="RequirementSettings"/>; none = an empty list).</summary>
        public static List<ResourceRequirement> Requirements(List<RequirementSettings> typed,
                                                             IReadOnlyDictionary<string, ResourceDispatcher> teams, string owner)
            => RequirementsOf(typed, teams, owner);

        /// <summary>Typed requirements resolved among <paramref name="teams"/>.</summary>
        public static List<ResourceRequirement> RequirementsOf(List<RequirementSettings> typed, IReadOnlyDictionary<string, ResourceDispatcher> teams, string owner)
        {
            var reqs = new List<ResourceRequirement>();
            foreach (var r in typed ?? new List<RequirementSettings>()) if (r != null) reqs.Add(DecodeRequirement(r, teams, owner));
            return reqs;
        }

        /// <summary>A typed requirement (count &gt;= 1; its query checked against the members' fields of its team).</summary>
        public static ResourceRequirement DecodeRequirement(RequirementSettings r, IReadOnlyDictionary<string, ResourceDispatcher> teams, string owner)
            => DecodeRequirement(EncodeRequirement(r.Count, r.Query, r.Label, r.Team), teams, owner);

        public static ResourceRequirement DecodeRequirement(string rec, IReadOnlyDictionary<string, ResourceDispatcher> teams, string owner)
        {
            var r = DecodeRequirement(rec);
            string[] p = rec.Split('#');
            string team = p.Length > 3 ? p[3].Trim() : "";
            if (team.Length == 0) return r;
            if (teams == null || !teams.TryGetValue(team, out var d))
                throw new ArgumentException(owner + ": the requirement '" + rec + "' names resource team '" + team + "', which does not exist.");
            string q = rec.Split('#').Length > 1 ? rec.Split('#')[1] : "";
            if (!string.IsNullOrWhiteSpace(q))
                ListFields.Check(ListQuerySpec.Decode(q), q, MemberFieldNames(d), ListFields.MemberRoots, false, owner + ", requirement '" + rec + "'");
            return new ResourceRequirement(r.Count, r.Query, r.Label, d);
        }

        public static ResourceRequirement DecodeRequirement(string rec)
        {
            string[] p = rec.Split('#');
            int count;
            if (!int.TryParse(p[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1)
                throw new FormatException("Resource requirement '" + rec + "': expected 'count#query#label' with count >= 1.");
            string q = p.Length > 1 ? p[1] : "";
            string label = p.Length > 2 ? p[2] : null;
            return new ResourceRequirement(count, string.IsNullOrWhiteSpace(q) ? null : ListQuerySpec.Decode(q).ToQuery(),
                                           string.IsNullOrEmpty(label) ? null : label);
        }
    }
}
