using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Downtime;
using SimuLean.Serialization;
using SimuLean.Storage;
using SimuLean.Transport;

namespace SimuLean.Headless
{
    /// <summary>
    /// Builds a headless (no-Unity) simulation model from a <see cref="SimulationConfig"/>.
    /// All Unity visual elements are replaced with <see cref="HeadlessVElement"/> instances so
    /// the engine runs without any 3-D overhead.
    /// Element creation is delegated to registered <see cref="IElementHandler"/> implementations
    /// via <see cref="ElementHandlerRegistry"/> — no switch statement required.
    /// </summary>
    public class HeadlessModelFactory
    {
        private readonly Dictionary<string, Element> createdElements = new Dictionary<string, Element>();
        private readonly List<DowntimeGenerator> downtimeGenerators = new List<DowntimeGenerator>();
        private readonly SimClock clock;
        private readonly bool enableLogging;
        private readonly ElementHandlerRegistry registry;
        private readonly Func<ElementConfig, IVElement> viewOf;
        private double warmupTime;
        private SimulationConfig lastConfig = new SimulationConfig();
        private readonly Dictionary<string, SimuLean.Resources.ResourceDispatcher> teams = new Dictionary<string, SimuLean.Resources.ResourceDispatcher>();
        private readonly List<SimuLean.Resources.TaskExecutor> executors = new List<SimuLean.Resources.TaskExecutor>();
        private readonly List<SimuLean.Resources.ResourceTransportLink> transportLinks = new List<SimuLean.Resources.ResourceTransportLink>();
        private readonly Dictionary<string, ElementConfig> elementConfigs = new Dictionary<string, ElementConfig>();
        private readonly List<WalkLink> walkLinks = new List<WalkLink>();

        /// <summary>The links whose items walk by themselves (views draw their walks).</summary>
        public IReadOnlyList<WalkLink> WalkLinks => walkLinks;

        /// <summary>Resource teams built by the last <see cref="BuildModel"/>, by id.</summary>
        public IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> Teams => teams;

        /// <summary>Every team member (all teams).</summary>
        public IReadOnlyList<SimuLean.Resources.TaskExecutor> Executors => executors;

        /// <summary>The clock the model runs on.</summary>
        public SimClock Clock => clock;

        /// <summary>The model it built last (<see cref="BuildModel"/>; read-only: the logic viewer's report of the running
        /// model, <see cref="SimuLean.Logic.ModelLogic.Live"/>).</summary>
        public SimulationConfig Config => lastConfig;

        /// <summary>The job recorder, when the run records its timeline (<see cref="SimuLean.Results.JobRecorder.Attach"/>).</summary>
        public SimuLean.Results.JobRecorder Jobs { get; internal set; }

        /// <summary>The warm-up of the last <see cref="BuildModel"/> (0 = none).</summary>
        public double WarmupTime => warmupTime;
        private readonly Dictionary<string, ListLink> lists = new Dictionary<string, ListLink>();

        /// <summary>Lists built from <see cref="SimulationConfig.Lists"/>, by id.</summary>
        public IReadOnlyDictionary<string, SimList> Lists
        {
            get { var d = new Dictionary<string, SimList>(); foreach (var kv in lists) d[kv.Key] = kv.Value.List; return d; }
        }

        /// <summary>The model's lists with their links, by list id.</summary>
        public IReadOnlyDictionary<string, ListLink> ListLinks => lists;

        private readonly Dictionary<string, List<DowntimeGenerator>> downtimesById = new Dictionary<string, List<DowntimeGenerator>>();

        /// <summary>The downtime generators of each downtime config (one per target), by config id.</summary>
        public IReadOnlyDictionary<string, List<DowntimeGenerator>> DowntimesById => downtimesById;

        private readonly Dictionary<string, SimuLean.Signals.Reaction> reactionsById = new Dictionary<string, SimuLean.Signals.Reaction>();
        /// <summary>The reactions built by the last <see cref="BuildModel"/>, by config id.</summary>
        public IReadOnlyDictionary<string, SimuLean.Signals.Reaction> ReactionsById => reactionsById;

        /// <summary>Element parameter: the network node the element stands at (<see cref="Element.Location"/>).</summary>
        public const string LocationKey = "location";

        /// <summary>Element parameter: the work points of the resources processing at it (<see cref="Element.WorkLocations"/>).</summary>
        public const string WorkNodesKey = "workNodes";

        /// <summary>The model's transport network (null when it has none).</summary>
        public SimuLean.Transport.TransportNetwork Network { get; private set; }

        /// <summary>The model's locations (network + travel matrices).</summary>
        public ModelSpace Space { get; private set; }

        public HeadlessModelFactory(SimClock clock, bool enableLogging = false)
            : this(clock, enableLogging, ElementHandlerRegistry.CreateDefault()) { }

        public HeadlessModelFactory(SimClock clock, bool enableLogging, ElementHandlerRegistry registry)
            : this(clock, enableLogging, registry, null) { }

        /// <param name="viewOf">The view of each element (the Unity scene gives its wrappers, so a scene run is the same
        /// model as a headless run); null or a null result = no view (<see cref="HeadlessVElement"/>).</param>
        public HeadlessModelFactory(SimClock clock, bool enableLogging, ElementHandlerRegistry registry,
                                    Func<ElementConfig, IVElement> viewOf)
        {
            this.clock         = clock;
            this.enableLogging = enableLogging;
            this.registry      = registry ?? ElementHandlerRegistry.CreateDefault();
            this.viewOf        = viewOf;
        }

        // =====================================================================
        //  Public API
        // =====================================================================

        /// <summary>
        /// Builds the complete model and returns a dictionary of id -> Element.
        /// </summary>
        public Dictionary<string, Element> BuildModel(SimulationConfig config)
        {
            // every expression parsed while building is checked once the model exists
            SimuLean.Expressions.ExpressionCheck.Begin();
            Dictionary<string, Element> built;
            List<SimuLean.Expressions.Expression> parsed;
            try { built = BuildModelCore(config); }
            finally { parsed = SimuLean.Expressions.ExpressionCheck.End(); }
            var setByReactions = new HashSet<string>(StringComparer.Ordinal);
            // the item-label writes the build sees besides the label records: a reaction's number, a source's arrivals table
            var labels = new SimuLean.Expressions.LabelWrites();
            foreach (var r in reactionsById.Values)
                foreach (var a in r.Actions)
                {
                    if (a.Kind == SimuLean.Signals.ReactionActionKind.SetElementProperty && !string.IsNullOrEmpty(a.Name)) setByReactions.Add(a.Name);
                    if (a.Kind == SimuLean.Signals.ReactionActionKind.SetItemLabel && a.Value.Expression == null && a.Value.IsSet)
                        labels.Add(a.Name, a.Value.Number, $"reaction '{r.Name}'");
                }
            foreach (var e in clock.Context.Elements)
                if (e is SourceBase source)
                    foreach (var (label, value, origin) in source.TableLabelWrites()) labels.Add(label, value, origin);
            var problems = SimuLean.Expressions.ExpressionCheck.Problems(parsed, clock.Context, setByReactions, labels);
            if (problems.Count > 0)
                throw new FormatException("The model's expressions read names it does not have:\n- " + string.Join("\n- ", problems));
            // a property an admission condition reads wakes its element when written: watched from now on
            clock.Context.WatchAdmissions(clock.Context.Elements);
            return built;
        }

        // The build, pass by pass, in this order: each pass needs what the ones before it made.
        private Dictionary<string, Element> BuildModelCore(SimulationConfig config)
        {
            ResetBuild(config);
            SetUpCalendar(config);
            // The model's data tables next: the snapshot every handler reads by name; a table without one is read
            // from its source now (CSV / .data / SQL) — never during the run.
            clock.Context.Data = SimuLean.Data.DataResolver.Build(config);
            BuildLocations(config);
            CreateElements(config);             // pass 1
            ApplyProperties(config);            // pass 1 (properties)
            BuildLists(config);                 // pass 1 (lists)
            BuildResources(config);             // pass 1a
            PrepareWiring(config);              // pass 1b
            WireConnections(config);            // pass 2
            ApplyWaitLimits();                  // pass 2b
            WireHandlers();                     // pass 3
            BuildDowntimes(config);             // pass 4
            BuildReactions(config);             // pass 5

            Log($"HeadlessModelFactory: built {createdElements.Count} elements " +
                $"and wired {config.Connections.Count} connections.");

            return createdElements;
        }

        private void ResetBuild(SimulationConfig config)
        {
            createdElements.Clear();
            downtimeGenerators.Clear();
            reactionsById.Clear();
            clock.Context.Signals.Clear();   // a model built again on this clock: the former subscriptions go
            clock.Context.ClearWarnings();
            clock.Context.ClearTeams();
            warmupTime = config.WarmupTime;
            lastConfig = config;
        }

        // Calendar first: handlers and downtime tables may convert dates to sim time.
        private void SetUpCalendar(SimulationConfig config)
        {
            clock.Context.Calendar = config.CreateCalendar();
            clock.Context.Holidays.Clear();
            if (config.Holidays != null)
                foreach (var h in config.Holidays)
                {
                    if (string.IsNullOrWhiteSpace(h)) continue;
                    if (!SimCalendar.TryParseDate(h, out System.DateTime d))
                        throw new FormatException($"Model holiday '{h}' is not a date.");
                    clock.Context.Holidays.Add(d.Date);
                }
        }

        // The model's locations: its transport network and travel matrices (elements stand at them, spatial
        // teams travel between them).
        private void BuildLocations(SimulationConfig config)
        {
            Network = TransportStorageConfigCodec.BuildNetwork(config.Network);
            Space = new ModelSpace { Network = Network };
            if (Network != null) Space.Traffic = new SimuLean.Resources.TrafficControl(config.Network.SegmentCapacity);
            foreach (var mc in config.TravelMatrices)
                if (mc != null && !string.IsNullOrEmpty(mc.Id)) Space.Matrices[mc.Id] = ResourceBuild.BuildMatrix(mc);
            foreach (var gc in config.WalkGrids)
                if (gc != null && !string.IsNullOrEmpty(gc.Id)) Space.Grids[gc.Id] = ResourceBuild.BuildGrid(gc);
        }

        // Pass 1 – create every element (and place it: parameter "location" = a network node)
        private void CreateElements(SimulationConfig config)
        {
            foreach (var elementConfig in config.Elements)
            {
                var element = CreateElement(elementConfig);
                if (element == null) continue;
                createdElements[elementConfig.Id] = element;
                string location = elementConfig.Settings?.Location ?? "";
                if (!string.IsNullOrEmpty(location))
                {
                    if (!Space.IsLocation(location))
                        throw new ArgumentException($"Element '{elementConfig.Name}': location '{location}' is neither a network node nor in a travel matrix.");
                    element.Location = location;
                }
                // Work points of the resources processing here: locations too.
                foreach (string node in elementConfig.Settings?.WorkNodes ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(node)) continue;
                    if (!Space.IsLocation(node))
                        throw new ArgumentException($"Element '{elementConfig.Name}': work node '{node}' is neither a network node nor in a travel matrix.");
                    element.WorkLocations.Add(node);
                }
            }
        }

        // Pass 1 (properties) – each element's user properties; references between elements resolved by name
        // once every element exists (an unknown name leaves the reference null, with a warning).
        private void ApplyProperties(SimulationConfig config)
        {
            var pendingRefs = new List<(Element owner, string name, string target)>();
            foreach (var elementConfig in config.Elements)
            {
                if (!createdElements.TryGetValue(elementConfig.Id, out Element owner)) continue;
                var records = elementConfig.Settings?.Properties;
                if (records == null || records.Count == 0) continue;
                foreach (var r in PropertyRecords.Apply(records, owner.Properties, elementConfig.Name))
                    pendingRefs.Add((owner, r.name, r.target));
            }
            if (pendingRefs.Count == 0) return;
            var byName = new Dictionary<string, Element>();
            foreach (var e in createdElements.Values) if (e.Name != null && !byName.ContainsKey(e.Name)) byName[e.Name] = e;
            foreach (var (owner, name, target) in pendingRefs)
            {
                byName.TryGetValue(target ?? "", out Element referenced);
                if (referenced == null)
                    SimLog.Warning($"[HeadlessModelFactory] '{owner.Name}': property '{name}' refers to '{target}', which is not an element of the model — left null.");
                owner.Properties.SetRef(name, referenced);
            }
        }

        // Pass 1 (lists) – one SimList + ListLink per list config
        private void BuildLists(SimulationConfig config)
        {
            lists.Clear();
            foreach (var lc in config.Lists)
            {
                if (lc == null || string.IsNullOrEmpty(lc.Id)) continue;
                var list = new SimList(string.IsNullOrEmpty(lc.Name) ? lc.Id : lc.Name, clock);
                list.DefineFields(lc.Fields);
                // its order reads its fields, item.x and origin.x; a bare name may be an item label (set during the run)
                if (!string.IsNullOrWhiteSpace(lc.DefaultOrderBy))
                {
                    var order = new ListQuerySpec { orderBy = lc.DefaultOrderBy };
                    ListFields.Check(order, lc.DefaultOrderBy, null, ListFields.ListRoots, true, $"List '{list.Name}', default order");
                    list.DefineExpressionKeys(order, "default order");
                }
                list.SetDefaultOrder(lc.DefaultOrderBy, lc.DefaultDescending);
                lists[lc.Id] = new ListLink(list);
            }
        }

        // Pass 1a – resource teams, then each element's resources: its own transport (a storage's moves, a milk run's
        // trains: ElementHandlerBase.ConfigureTransport), its setup, its process (ConfigureProcessUse: an assembly
        // station's tasks) and the checks of what only some types do.
        private void BuildResources(SimulationConfig config)
        {
            teams.Clear(); executors.Clear(); transportLinks.Clear(); walkLinks.Clear(); elementConfigs.Clear();
            foreach (var ec in config.Elements) if (ec != null && ec.Id != null) elementConfigs[ec.Id] = ec;
            foreach (var tc in config.ResourceTeams)
                if (tc != null && !string.IsNullOrEmpty(tc.Id))
                    teams[tc.Id] = ResourceBuild.BuildTeam(tc, clock, executors, Space);
            // Members of different teams with one name: the results tell them apart by team; say so.
            var memberNames = new HashSet<string>();
            foreach (var ex in executors)
                if (!memberNames.Add(ex.Name))
                    SimLog.Warning($"[SimuLean] Several resources are named '{ex.Name}': name each member after its team " +
                                   "(the results report them as team/member).");
            foreach (var kv in elementConfigs)
            {
                createdElements.TryGetValue(kv.Key, out Element st);
                var hooks = st != null ? HooksOf(kv.Value) : null;
                if (hooks != null && !hooks.ConfigureTransport(st, kv.Value, teams, clock)) continue;
                // A station's setup before its process (its crew is a use of a team too).
                var setup = ResourceBuild.SetupOf(kv.Value, teams, st, clock);
                if (setup != null && st is ISetupStation setupStation) setupStation.Setup = setup;
                var use = ResourceBuild.ProcessUse(kv.Value, teams, st, clock);
                if (use != null && use.Release == SimuLean.Resources.ProcessRelease.AtElement)
                {
                    // where the resources come back: an element of this model, by name or id
                    string at = kv.Value.Settings.ProcessBy?.ReleaseAt ?? "";
                    use.ReleaseAt = (string.IsNullOrWhiteSpace(at) ? null : Find(at.Trim()))
                        ?? throw new FormatException($"Element '{kv.Value.Name}': its resources are released at an element, but '{at}' is not one of the model.");
                }
                hooks?.ConfigureProcessUse(st, kv.Value, teams, use);
                // only the types with the ReserveExit group (Multi-Server, Combiner, Picking Station) have the setting
                if (kv.Value.Settings?.ReserveExit == true) st.ReserveExit = true;
                if (use != null && use.AcquireBeforeEntry)
                {
                    // the Multi-Server only (the Combiner gathered its components and the Picking Station
                    // its stock between the grant and the work, and its timer ran on meanwhile)
                    if (hooks?.JobsAcquireBeforeEntry != true)
                        throw new FormatException($"Element '{kv.Value.Name}' secures its resources before the part " +
                                                  "(processBy.acquireBeforeEntry), which only the Multi-Server does: switch it off.");
                    if (use.MaxWait != null && use.MaxWait.OnTimeout == TimeoutAction.Reroute)
                        throw new FormatException($"Element '{kv.Value.Name}': processBy.onTimeout = Reroute sends a job's item " +
                                                  "elsewhere, but with processBy.acquireBeforeEntry the wait comes before the part — " +
                                                  "there is no item to send (use GiveUp or KeepWaiting).");
                }
                if (use != null && use.MaxWait != null && use.MaxWait.OnTimeout == TimeoutAction.Reroute)
                {
                    // a job that gives up sends its item to the timeout target unprocessed: the Multi-Server's
                    if (hooks?.JobsReroute != true)
                        throw new FormatException($"Element '{kv.Value.Name}': processBy.onTimeout = Reroute sends a job's item " +
                                                  "elsewhere, which only the Multi-Server does (GiveUp asks again; KeepWaiting only records).");
                    // its target is wired with the links (pass 2b)
                }
                if (use == null) continue;
                // only the types with the Process group have processBy, and each is a resource user
                ((SimuLean.Resources.IResourceUser)createdElements[kv.Key]).Resources = use;
            }
        }

        // Pass 1b – what a type sets up before the links (a combiner's input order: ElementHandlerBase.PrepareWiring).
        private void PrepareWiring(SimulationConfig config)
        {
            foreach (var elementConfig in config.Elements)
                if (createdElements.TryGetValue(elementConfig.Id, out Element el))
                    HooksOf(elementConfig)?.PrepareWiring(el, elementConfig, createdElements);
        }

        // Pass 2 – wire connections.
        // Every connection that feeds a source's OUTPUT (General / Simple to an element, StockInput to a station's
        // stock port) must share a SINGLE link per source: creating one link per connection replaces the source's
        // output each time, leaving only the last target connected (a feed into two stations'
        // stock starved the first). Lists and the picking returns port are wired on their own. A named port's output
        // (PortOutput) is grouped under "owner/port", its source being the port element.
        private void WireConnections(SimulationConfig config)
        {
            var targetsBySource = new Dictionary<string, List<Element>>();
            var portSources = new Dictionary<string, Element>();
            foreach (var connection in config.Connections)
            {
                string ct = ConnectionConfig.EffectiveType(connection);   // no type or "Simple" = General
                Element target;
                switch (ct)
                {
                    case "General":
                        if (!createdElements.TryGetValue(connection.TargetId, out target))
                            throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': the target is not an element of the model.");
                        break;
                    case "StockInput":
                        target = StockPortOf(connection);
                        break;
                    case "PortInput":
                        target = PortOf(connection, connection.TargetId);
                        break;
                    case "PortOutput":
                    {
                        Element port = PortOf(connection, connection.SourceId);
                        if (!createdElements.TryGetValue(connection.TargetId, out target))
                            throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': the target is not an element of the model.");
                        string key = connection.SourceId + "/" + (connection.Port ?? "");
                        portSources[key] = port;
                        if (!targetsBySource.TryGetValue(key, out var fromPort)) targetsBySource[key] = fromPort = new List<Element>();
                        fromPort.Add(target);
                        continue;
                    }
                    case "ReturnsOutput":
                    case "PushToList":
                    case "PullFromList":
                        CreateConnection(connection);
                        continue;
                    default:
                        throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': unknown connection type '{ct}' " +
                                                  "(General, StockInput, PortInput, PortOutput, ReturnsOutput, PushToList, PullFromList).");
                }
                if (!createdElements.ContainsKey(connection.SourceId))
                    throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': the source is not an element of the model.");
                if (!targetsBySource.TryGetValue(connection.SourceId, out var list))
                    targetsBySource[connection.SourceId] = list = new List<Element>();
                list.Add(target);
            }

            foreach (var kvp in targetsBySource)
            {
                Element source = portSources.TryGetValue(kvp.Key, out Element portSource) ? portSource : createdElements[kvp.Key];
                var targets = kvp.Value;
                var transportUse = elementConfigs.TryGetValue(kvp.Key, out var srcCfg) ? ResourceBuild.TransportUse(srcCfg, teams, source) : null;
                var walk = srcCfg != null ? ResourceBuild.WalkTravel(srcCfg, Space, clock.Context.Calendar.SecondsPerUnit) : null;
                if (walk != null && transportUse != null)
                    SimLog.Warning($"[HeadlessModelFactory] '{source.Name}': its items are carried by a transport team and also walk — the team carries them.");
                if (targets.Count > 0 && walk != null && transportUse == null)
                {
                    // Its items walk by themselves to the next elements (a patient: the item is a mobile entity).
                    var wl = WalkLink.Create(source, targets, walk);
                    wl.WaitAtDoor = srcCfg.Settings?.WalkBy?.WaitAtDoor == true;
                    walkLinks.Add(wl);
                    Log($"  Wired {source.Name} -> [{string.Join(", ", targets.ConvertAll(t => t.Name))}] (walking)");
                }
                else if (targets.Count > 0 && transportUse != null)
                {
                    // Its items are carried by resources to the next elements.
                    var tl = SimuLean.Resources.ResourceTransportLink.Create(source, targets, transportUse);
                    ResourceBuild.ApplyTransportTimes(tl, srcCfg, clock);
                    transportLinks.Add(tl);
                    Log($"  Wired {source.Name} -> [{string.Join(", ", targets.ConvertAll(t => t.Name))}] (transport by resources)");
                }
                else if (targets.Count > 0)
                {
                    GeneralLink.CreateLink(source, targets);
                    Log($"  Wired {source.Name} -> [{string.Join(", ", targets.ConvertAll(t => t.Name))}]");
                }
            }
        }

        // Pass 2b – max waits of queued items and list pulls, once the links exist
        private void ApplyWaitLimits()
        {
            foreach (var kv in elementConfigs)
                if (createdElements.TryGetValue(kv.Key, out Element limited)) WaitLimits(kv.Value, limited);
            // a station securing its resources before the part learns of an item from the link that could not hand it
            // over; a list it pulls from never offers one (its pull matches only what it can take now)
            foreach (var kv in elementConfigs)
            {
                if (!createdElements.TryGetValue(kv.Key, out Element pc) || !pc.PreparesForEntry) continue;
                var input = pc.GetInput();
                bool fromList = input is ListLink;
                if (input is InputFanIn fan) foreach (var link in fan.Links) fromList |= link is ListLink;
                if (fromList)
                    throw new FormatException($"Element '{kv.Value.Name}' pulls from a list and secures its resources before the part " +
                                              $"(processBy.acquireBeforeEntry): a list offers only what it can take now — switch one off.");
            }
        }

        // Pass 3 – element-specific post-build wiring (no-op for most handlers)
        private void WireHandlers()
        {
            foreach (var kvp in createdElements)
            {
                // by id from the map built in pass 1a (not a list search per element)
                if (elementConfigs.TryGetValue(kvp.Key, out var elementConfig) && registry.TryGetHandler(elementConfig.Type, out var handler))
                    handler.Wire(kvp.Value, elementConfig, createdElements);
            }
        }

        // Pass 4 – downtime generators (need the finished element graph)
        private void BuildDowntimes(SimulationConfig config)
        {
            downtimesById.Clear();
            foreach (var dc in config.Downtimes)
            {
                var built = DowntimeBuilder.Build(dc, createdElements, clock, teams);
                // a disabled downtime is built (its settings checked, its statistics read 0) but never started
                if (dc?.Settings?.Enabled != false) downtimeGenerators.AddRange(built);
                if (dc?.Id != null) downtimesById[dc.Id] = built;
            }
        }

        // Pass 5 – reactions: subscribed to the finished elements' signals
        private void BuildReactions(SimulationConfig config)
        {
            foreach (var rc in config.Reactions)
            {
                if (rc == null) continue;
                var reaction = ReactionBuilder.Build(rc, createdElements, clock, teams);
                if (reactionsById.ContainsKey(reaction.Name)) throw new FormatException($"Two reactions are called '{reaction.Name}'.");
                reactionsById[reaction.Name] = reaction;
            }
        }

        // The build hooks of an element's type (null: its handler has none, or no handler).
        private ElementHandlerBase HooksOf(ElementConfig config)
            => config != null && config.Type != null && registry.TryGetHandler(config.Type, out var handler) ? handler as ElementHandlerBase : null;

        private ElementHandlerBase HooksOf(string elementId)
            => elementId != null && elementConfigs.TryGetValue(elementId, out var config) ? HooksOf(config) : null;

        // A Queue's reneging (maxWait / onTimeout / timeoutTarget) and a list puller's max wait (pullMaxWait). A key the
        // element's type does not use fails the build, as a Reroute behind a list or carriers (only plain connections can
        // send an item elsewhere) or without a target.
        private void WaitLimits(ElementConfig ec, Element element)
        {
            string owner = "Element '" + ec.Name + "'";
            if (element is SimuLean.Resources.IResourceUser user && user.Resources?.MaxWait?.OnTimeout == TimeoutAction.Reroute)
            {
                string to = ec.Settings.ProcessBy?.TimeoutTarget ?? "";
                var target = (string.IsNullOrWhiteSpace(to) ? null : Find(to.Trim()))
                    ?? throw new FormatException($"{owner}: processBy.onTimeout = Reroute needs " +
                                                 $"processBy.timeoutTarget, an element of the model ('{to}' is not).");
                element.SetTimeoutTarget(target);
            }
            // a Queue's reneging: its own settings (no other type declares them)
            if (ec.Settings is QueueSettings qs && element is ItemsQueue queue)
            {
                var action = WaitLimit.ActionOf(qs.OnTimeout ?? "", owner);
                var limit = WaitLimit.Parse(qs.MaxWait ?? "", action, owner, "maxWait");
                if (limit != null)
                {
                    if (action != TimeoutAction.KeepWaiting)
                    {
                        if (!(queue.GetOutput() is GeneralLink))
                            throw new FormatException($"{owner}: onTimeout = {action} sends a timed-out item to the timeout " +
                                                      "target, which needs plain connections as its output (not a list or carriers): use KeepWaiting.");
                        string to = qs.TimeoutTarget ?? "";
                        var target = (string.IsNullOrWhiteSpace(to) ? null : Find(to.Trim()))
                            ?? throw new FormatException($"{owner}: onTimeout = {action} needs timeoutTarget, " +
                                                         $"an element of the model ('{to}' is not) — e.g. a Sink for the items lost.");
                        queue.SetTimeoutTarget(target);
                    }
                    queue.MaxWait = limit;
                    queue.DeclareWaitLimit(WaitKind.Queue);
                }
            }
            string pull = ec.Settings?.PullMaxWait ?? "";
            var pullLimit = WaitLimit.Parse(pull, TimeoutAction.KeepWaiting, owner, "pullMaxWait");
            if (pullLimit == null) return;
            ListLink pulledFrom = null;
            var input = element.GetInput();
            if (input is ListLink l) pulledFrom = l;
            else if (input is InputFanIn fan) foreach (var link in fan.Links) if (link is ListLink fl) { pulledFrom = fl; break; }
            if (pulledFrom == null)
                throw new FormatException($"{owner}: pullMaxWait limits its wait for a list entry, but it pulls from no list.");
            pulledFrom.SetPullLimit(element, pullLimit);
            element.DeclareWaitLimit(WaitKind.ListPull);
        }

        /// <summary>Downtime generators built by the last <see cref="BuildModel"/> that <see cref="StartModel"/> starts (a disabled
        /// downtime's are only in <see cref="DowntimesById"/>).</summary>
        public IReadOnlyList<DowntimeGenerator> DowntimeGenerators => downtimeGenerators;

        /// <summary>
        /// Starts a built model: every element's <c>Start()</c>, then every downtime
        /// generator (generators must start after the elements they stop), then the
        /// warm-up reset when <see cref="SimulationConfig.WarmupTime"/> is set.
        /// </summary>
        /// <summary>Elements and resources keep their state logs (default: the timeline and the inspector read them). A
        /// headless run that records no timeline turns it off before <see cref="StartModel"/>: memory flat with the run.</summary>
        public bool KeepStateLogs { get; set; } = true;

        public void StartModel()
        {
            foreach (var element in createdElements.Values) element.KeepStateLog = KeepStateLogs;
            foreach (var ex in executors) ex.KeepStateLog = KeepStateLogs;
            foreach (var link in lists.Values) { link.Reset(); link.List.Reset(); }
            foreach (var tl in transportLinks) tl.Reset();
            foreach (var wl in walkLinks) wl.Reset();
            Space?.Traffic?.Reset();
            foreach (var element in createdElements.Values)
                element.Start();
            foreach (var ex in executors) ex.Start();        // resources after the elements,
            foreach (var d in teams.Values) d.Start();       // then their dispatchers list them as free
            foreach (var link in lists.Values)
                link.Start();                       // first pulls, after the elements are started
            foreach (var generator in downtimeGenerators)
                generator.Start();
            foreach (var reaction in reactionsById.Values) reaction.Reset();
            foreach (var reaction in reactionsById.Values) reaction.Start();   // time signals: their first tick
            // elements, resources, generators and teams: every statistic covers the same period
            var warmup = new List<IWarmupStatistics>(createdElements.Values);
            warmup.AddRange(executors);
            warmup.AddRange(downtimeGenerators);
            warmup.AddRange(teams.Values);
            warmup.AddRange(reactionsById.Values);
            Monitor = new RunMonitor(clock, createdElements, lastConfig, downtimeGenerators, teams.Values);   // end condition, empty / deadlock
            warmup.Add(Monitor);   // the end condition's count starts with the observed period
            Element.ScheduleWarmup(clock, warmup, warmupTime);
        }

        /// <summary>The run control of the started model (end condition, model empty, deadlock).</summary>
        public RunMonitor Monitor { get; private set; }

        /// <summary>Returns the element with the given ID, or null.</summary>
        public Element GetElement(string id) =>
            createdElements.TryGetValue(id, out var e) ? e : null;

        /// <summary>
        /// The element with the display name <paramref name="name"/> (the ids of a scene's elements are
        /// <c>Type_Name_instanceId</c>), or the one with that id; null when there is none. Two elements of that name fail.
        /// </summary>
        public Element Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (createdElements.TryGetValue(name, out var byId)) return byId;
            Element found = null;
            foreach (var kv in createdElements)
            {
                if (kv.Value.Name != name) continue;
                if (found != null)
                    throw new InvalidOperationException($"Several elements are named '{name}' ({string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(createdElements, x => x.Value.Name == name), x => x.Key))}): use the id.");
                found = kv.Value;
            }
            return found;
        }

        /// <summary>Returns a read-only view of all created elements.</summary>
        public IReadOnlyDictionary<string, Element> GetAllElements() => createdElements;

        // =====================================================================
        //  Element factory dispatch
        // =====================================================================

        private Element CreateElement(ElementConfig config)
        {
            IVElement vElement = viewOf?.Invoke(config) ?? new HeadlessVElement(enableLogging);

            if (registry.TryGetHandler(config.Type, out var handler))
            {
                Element built = handler.Build(config, clock, vElement);
                // Input / output strategies are common to every element: applied here for all
                // types (handlers that already did it set the same strategy), so a config that
                // carries them never silently falls back to the defaults headless.
                if (built != null)
                {
                    ElementBuildHelpers.ApplyOutputStrategy(built, config);
                    ElementBuildHelpers.ApplyLabelAssignments(built, config);
                    ElementBuildHelpers.ApplyTallies(built, config);
                    if ((handler as ElementHandlerBase)?.AppliesInputStrategy != false) ElementBuildHelpers.ApplyInputStrategy(built, config);
                }
                return built;
            }

            throw new FormatException($"Element '{config.Name}': unknown element type '{config.Type}'.");
        }

        // =====================================================================
        //  Connection wiring
        // =====================================================================

        private void CreateConnection(ConnectionConfig connection)
        {
            if (connection.ConnectionType == "PushToList" || connection.ConnectionType == "PullFromList")
            {
                bool push = connection.ConnectionType == "PushToList";
                string elementId = push ? connection.SourceId : connection.TargetId;
                string listId = push ? connection.TargetId : connection.SourceId;
                if (!createdElements.TryGetValue(elementId, out Element el) || !lists.TryGetValue(listId, out ListLink link))
                {
                    throw new FormatException($"{connection.ConnectionType}: element '{elementId}' or list '{listId}' is not in the model.");
                }
                if (push)
                {
                    link.AddOrigin(el);
                    // Its items are carried by its transport resources to the pullers. Not a
                    // storage's: its own retrieval already carries the item to the puller.
                    var use = elementConfigs.TryGetValue(elementId, out var holderCfg) && HooksOf(holderCfg)?.ListPushesByTransport != false
                        ? ResourceBuild.TransportUse(holderCfg, teams, el) : null;
                    if (use != null) ResourceBuild.ApplyTransportTimes(link.SetTransport(el, use), holderCfg, clock);
                }
                else
                {
                    // A combiner input (its Input: 0 = main, k = component input k-1) may pull too, and a named port
                    // of an IPortedElement.
                    Element puller = el;
                    if (!string.IsNullOrEmpty(connection.Port))
                        puller = PortOf(connection, elementId);
                    else if (connection.Input is int port && port >= 0 && HooksOf(elementId)?.InputPort(el, port) is Element inputPort)
                        puller = inputPort;
                    string queryText = connection.Query ?? "";
                    var query = ListQuerySpec.Decode(queryText);
                    ListFields.Check(query, queryText, null, ListFields.ListRoots, true, $"'{el.Name}' pulling from list '{listId}'");
                    link.List.DefineExpressionKeys(query, $"query of '{el.Name}'");
                    link.AddPuller(puller, query.ToQuery());
                }
                Log($"  Wired {(push ? el.Name + " -> list " + listId : "list " + listId + " -> " + el.Name)}");
                return;
            }

            if (!createdElements.TryGetValue(connection.SourceId, out Element source))
            {
                throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': the source is not an element of the model.");
            }
            if (!createdElements.TryGetValue(connection.TargetId, out Element target))
            {
                throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': the target is not an element of the model.");
            }

            switch (connection.ConnectionType)
            {
                case "ReturnsOutput":
                {
                    // Picking station returns port → returns destination.
                    Element returns = HooksOf(connection.SourceId)?.ReturnsPort(source);
                    if (returns == null)
                    {
                        throw new FormatException($"ReturnsOutput source '{connection.SourceId}' is not a picking station.");
                    }
                    GeneralLink.CreateLink(returns, new List<Element> { target });
                    Log($"  Wired {source.Name}.ReturnsPort -> {target.Name}");
                    break;
                }

                default:
                    throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': '{connection.ConnectionType}' is wired with the source's output link.");
            }
        }

        // A named port of an IPortedElement (the connection's Port): PortInput's target, PortOutput's source.
        private Element PortOf(ConnectionConfig connection, string ownerId)
        {
            if (!createdElements.TryGetValue(ownerId, out Element owner))
                throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': '{ownerId}' is not an element of the model.");
            string portName = connection.Port ?? "";
            return (owner as IPortedElement)?.GetPort(portName)
                   ?? throw new FormatException($"{connection.ConnectionType}: '{owner.Name}' has no port '{portName}'.");
        }

        // Picking / Assembly station material feed: the station's SKU stock port (a destination of the feed's output).
        private Element StockPortOf(ConnectionConfig connection)
        {
            if (!createdElements.TryGetValue(connection.TargetId, out Element target))
                throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': the target is not an element of the model.");
            SkuStockPort port = HooksOf(connection.TargetId)?.StockPort(target);
            if (port == null)
                throw new FormatException($"StockInput target '{connection.TargetId}' is not a picking or assembly station.");
            return port;
        }

        // =====================================================================
        //  Logging
        // =====================================================================

        private void Log(string message)
        {
            if (enableLogging)
                SimLog.Info(message);
        }
    }
}
