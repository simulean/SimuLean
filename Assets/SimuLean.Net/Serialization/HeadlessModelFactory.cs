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

        /// <summary>The job recorder, when the run records its timeline (<see cref="SimuLean.Results.JobRecorder.Attach"/>, G3d).</summary>
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
            createdElements.Clear();
            downtimeGenerators.Clear();
            reactionsById.Clear();
            clock.Context.Signals.Clear();   // a model built again on this clock: the former subscriptions go
            warmupTime = config.WarmupTime;
            lastConfig = config;

            // Calendar first: handlers and downtime tables may convert dates to sim time.
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

            // The model's data tables next: the snapshot every handler reads by name; a table without one is read
            // from its source now (CSV / .data / SQL) — never during the run.
            clock.Context.Data = SimuLean.Data.DataResolver.Build(config);

            // The model's locations: its transport network and travel matrices (elements stand at them, spatial
            // teams travel between them).
            Network = TransportStorageConfigCodec.BuildNetwork(config.Network);
            Space = new ModelSpace { Network = Network };
            if (Network != null) Space.Traffic = new SimuLean.Resources.TrafficControl(config.Network.SegmentCapacity);
            foreach (var mc in config.TravelMatrices)
                if (mc != null && !string.IsNullOrEmpty(mc.Id)) Space.Matrices[mc.Id] = ResourceBuild.BuildMatrix(mc);
            foreach (var gc in config.WalkGrids)
                if (gc != null && !string.IsNullOrEmpty(gc.Id)) Space.Grids[gc.Id] = ResourceBuild.BuildGrid(gc);

            // Pass 1 – create every element (and place it: parameter "location" = a network node)
            foreach (var elementConfig in config.Elements)
            {
                var element = CreateElement(elementConfig);
                if (element == null) continue;
                createdElements[elementConfig.Id] = element;
                string location = elementConfig.GetParameter<string>(LocationKey, "");
                if (!string.IsNullOrEmpty(location))
                {
                    if (!Space.IsLocation(location))
                        throw new ArgumentException($"Element '{elementConfig.Name}': location '{location}' is neither a network node nor in a travel matrix.");
                    element.Location = location;
                }
                // Work points of the resources processing here (S4): locations too.
                foreach (string node in elementConfig.GetParameter<List<string>>(WorkNodesKey) ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(node)) continue;
                    if (!Space.IsLocation(node))
                        throw new ArgumentException($"Element '{elementConfig.Name}': work node '{node}' is neither a network node nor in a travel matrix.");
                    element.WorkLocations.Add(node);
                }
            }

            // Pass 1 (properties) – each element's user properties; references between elements resolved by name
            // once every element exists (an unknown name leaves the reference null, with a warning).
            var pendingRefs = new List<(Element owner, string name, string target)>();
            foreach (var elementConfig in config.Elements)
            {
                if (!createdElements.TryGetValue(elementConfig.Id, out Element owner)) continue;
                var records = elementConfig.GetParameter<List<string>>(PropertyRecords.Key, null);
                if (records == null || records.Count == 0) continue;
                foreach (var r in PropertyRecords.Apply(records, owner.Properties, elementConfig.Name))
                    pendingRefs.Add((owner, r.name, r.target));
            }
            if (pendingRefs.Count > 0)
            {
                var byName = new Dictionary<string, Element>();
                foreach (var e in createdElements.Values) if (e.Name != null && !byName.ContainsKey(e.Name)) byName[e.Name] = e;
                foreach (var (owner, name, target) in pendingRefs)
                {
                    byName.TryGetValue(target ?? "", out Element referenced);
                    if (referenced == null)
                        SimLog.Warning($"[HeadlessModelFactory] '{owner.GetName()}': property '{name}' refers to '{target}', which is not an element of the model — left null.");
                    owner.Properties.SetRef(name, referenced);
                }
            }

            // Pass 1 (lists) – one SimList + ListLink per list config
            lists.Clear();
            foreach (var lc in config.Lists)
            {
                if (lc == null || string.IsNullOrEmpty(lc.Id)) continue;
                var list = new SimList(string.IsNullOrEmpty(lc.Name) ? lc.Id : lc.Name, clock);
                list.SetDefaultOrder(lc.DefaultOrderBy, lc.DefaultDescending);
                list.DefineFields(lc.Fields);
                lists[lc.Id] = new ListLink(list);
            }

            // Pass 1a – resource teams, then each element's process resources
            teams.Clear(); executors.Clear(); transportLinks.Clear(); walkLinks.Clear(); elementConfigs.Clear();
            foreach (var ec in config.Elements) if (ec != null && ec.Id != null) elementConfigs[ec.Id] = ec;
            foreach (var tc in config.ResourceTeams)
                if (tc != null && !string.IsNullOrEmpty(tc.Id))
                    teams[tc.Id] = ResourceBuild.BuildTeam(tc, clock, executors, Space);
            // Members of different teams with one name: the results tell them apart by team; say so.
            var memberNames = new HashSet<string>();
            foreach (var ex in executors)
                if (!memberNames.Add(ex.GetName()))
                    SimLog.Warning($"[SimuLean] Several resources are named '{ex.GetName()}': name each member after its team " +
                                   "(the results report them as team/member).");
            foreach (var kv in elementConfigs)
            {
                // A storage's internal moves (relocations, retrievals to its location) use its transport team.
                if (createdElements.TryGetValue(kv.Key, out Element st) && st is StorageSystem storage)
                    storage.Transport = ResourceBuild.TransportUse(kv.Value, teams, st);
                var use = ResourceBuild.ProcessUse(kv.Value, teams, st, clock);
                if (use == null) continue;
                if (use.Release == SimuLean.Resources.ProcessRelease.AtElement)
                {
                    // where the resources come back (I6): an element of this model, by name or id
                    string at = kv.Value.GetParameter<string>(ResourceUseKeys.ProcessReleaseAt, "");
                    use.ReleaseAt = (string.IsNullOrWhiteSpace(at) ? null : Find(at.Trim()))
                        ?? throw new FormatException($"Element '{kv.Value.Name}': its resources are released at an element, but '{at}' is not one of the model.");
                }
                if (createdElements.TryGetValue(kv.Key, out Element e) && e is SimuLean.Resources.IResourceUser user)
                    user.Resources = use;
                else
                    throw new FormatException($"Element '{kv.Value.Name}' has process resources but its type does not use them.");
            }

            // Pass 1b – combiner input order: ids of the upstream elements, [0] = main input.
            // Resolved lazily when the links are created (Combiner.ResolveInputFor).
            foreach (var elementConfig in config.Elements)
            {
                if (!createdElements.TryGetValue(elementConfig.Id, out Element el) || !(el is Combiner comb)) continue;
                var orderIds = elementConfig.GetParameter<List<string>>("inputOrder");
                if (orderIds == null || orderIds.Count == 0) continue;
                comb.InputOrderProvider = () => orderIds.ConvertAll(id => createdElements.TryGetValue(id, out Element e) ? e : null);
            }

            // Pass 2 – wire connections.
            // Every connection that feeds a source's OUTPUT (General / Simple to an element, StockInput to a station's
            // stock port) must share a SINGLE link per source: creating one link per connection replaces the source's
            // output each time, leaving only the last target connected (a feed into two stations'
            // stock starved the first). Lists and the picking returns port are wired on their own.
            var targetsBySource = new Dictionary<string, List<Element>>();
            foreach (var connection in config.Connections)
            {
                string ct = connection.ConnectionType ?? "General";
                Element target;
                switch (ct)
                {
                    case "General":
                    case "Simple":
                        if (!createdElements.TryGetValue(connection.TargetId, out target))
                            throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': the target is not an element of the model.");
                        break;
                    case "StockInput":
                        target = StockPortOf(connection);
                        break;
                    case "ReturnsOutput":
                    case "PushToList":
                    case "PullFromList":
                        CreateConnection(connection);
                        continue;
                    default:
                        throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': unknown connection type '{ct}' " +
                                                  "(General, StockInput, ReturnsOutput, PushToList, PullFromList).");
                }
                if (!createdElements.ContainsKey(connection.SourceId))
                    throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': the source is not an element of the model.");
                if (!targetsBySource.TryGetValue(connection.SourceId, out var list))
                    targetsBySource[connection.SourceId] = list = new List<Element>();
                list.Add(target);
            }

            foreach (var kvp in targetsBySource)
            {
                Element source = createdElements[kvp.Key];
                var targets = kvp.Value;
                var transportUse = elementConfigs.TryGetValue(kvp.Key, out var srcCfg) ? ResourceBuild.TransportUse(srcCfg, teams, source) : null;
                var walk = srcCfg != null ? ResourceBuild.WalkTravel(srcCfg, Space, clock.Context.Calendar.SecondsPerUnit) : null;
                if (walk != null && transportUse != null)
                    SimLog.Warning($"[HeadlessModelFactory] '{source.GetName()}': its items are carried by a transport team and also walk — the team carries them.");
                if (targets.Count > 0 && walk != null && transportUse == null)
                {
                    // Its items walk by themselves to the next elements (a patient: the item is a mobile entity).
                    var wl = WalkLink.Create(source, targets, walk);
                    wl.WaitAtDoor = srcCfg.GetParameter<bool>(ResourceUseKeys.WalkWaitAtDoor, false);
                    walkLinks.Add(wl);
                    Log($"  Wired {source.GetName()} -> [{string.Join(", ", targets.ConvertAll(t => t.GetName()))}] (walking)");
                }
                else if (targets.Count > 0 && transportUse != null)
                {
                    // Its items are carried by resources to the next elements.
                    var tl = SimuLean.Resources.ResourceTransportLink.Create(source, targets, transportUse);
                    ResourceBuild.ApplyTransportTimes(tl, srcCfg, clock);
                    transportLinks.Add(tl);
                    Log($"  Wired {source.GetName()} -> [{string.Join(", ", targets.ConvertAll(t => t.GetName()))}] (transport by resources)");
                }
                else if (targets.Count > 0)
                {
                    GeneralLink.CreateLink(source, targets);
                    Log($"  Wired {source.GetName()} -> [{string.Join(", ", targets.ConvertAll(t => t.GetName()))}]");
                }
            }


            // Pass 3 – element-specific post-build wiring (no-op for most handlers)
            foreach (var kvp in createdElements)
            {
                // by id from the map built in pass 1 (a list search per element was O(n²) on every build)
                if (elementConfigs.TryGetValue(kvp.Key, out var elementConfig) && registry.TryGetHandler(elementConfig.Type, out var handler))
                    handler.Wire(kvp.Value, elementConfig, createdElements);
            }

            // Pass 4 – downtime generators (need the finished element graph)
            downtimesById.Clear();
            foreach (var dc in config.Downtimes)
            {
                var built = DowntimeBuilder.Build(dc, createdElements, clock);
                downtimeGenerators.AddRange(built);
                if (dc?.Id != null) downtimesById[dc.Id] = built;
            }

            // Pass 5 – reactions: subscribed to the finished elements' signals
            foreach (var rc in config.Reactions)
            {
                if (rc == null) continue;
                var reaction = ReactionBuilder.Build(rc, createdElements, clock, teams);
                if (reactionsById.ContainsKey(reaction.Name)) throw new FormatException($"Two reactions are called '{reaction.Name}'.");
                reactionsById[reaction.Name] = reaction;
            }

            Log($"HeadlessModelFactory: built {createdElements.Count} elements " +
                $"and wired {config.Connections.Count} connections.");

            return createdElements;
        }

        /// <summary>Downtime generators built by the last <see cref="BuildModel"/>.</summary>
        public IReadOnlyList<DowntimeGenerator> DowntimeGenerators => downtimeGenerators;

        /// <summary>
        /// Starts a built model: every element's <c>Start()</c>, then every downtime
        /// generator (generators must start after the elements they stop), then the
        /// warm-up reset when <see cref="SimulationConfig.WarmupTime"/> is set.
        /// </summary>
        /// <summary>Elements and resources keep their state logs (default: the timeline and the inspector read them). A
        /// headless run that records no timeline turns it off before <see cref="StartModel"/>: memory flat with the run
        /// (review2 #19).</summary>
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
            Element.ScheduleWarmup(clock, warmup, warmupTime);
            Monitor = new RunMonitor(clock, createdElements, lastConfig);   // end condition, empty / deadlock (S2)
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
                if (kv.Value.GetName() != name) continue;
                if (found != null)
                    throw new InvalidOperationException($"Several elements are named '{name}' ({string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(createdElements, x => x.Value.GetName() == name), x => x.Key))}): use the id.");
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
                    if (!(built is Combiner)) ElementBuildHelpers.ApplyInputStrategy(built, config);   // combiner: pull mode
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
                    // Its items are carried by its transport resources to the pullers (as a direct link).
                    var use = elementConfigs.TryGetValue(elementId, out var holderCfg) ? ResourceBuild.TransportUse(holderCfg, teams, el) : null;
                    if (use != null) ResourceBuild.ApplyTransportTimes(link.SetTransport(el, use), holderCfg, clock);
                }
                else
                {
                    // A combiner input (parameter "port": 0 = main, k = component input k-1) may pull too.
                    Element puller = el;
                    int port = connection.GetParameter<int>("port", -1);
                    if (port >= 0 && el is Combiner comb) puller = comb.GetInputPort(port);
                    link.AddPuller(puller, ListQuerySpec.Decode(connection.GetParameter<string>("query", "")).ToQuery());
                }
                Log($"  Wired {(push ? el.GetName() + " -> list " + listId : "list " + listId + " -> " + el.GetName())}");
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
                    if (!(source is PickingStation pick))
                    {
                        throw new FormatException($"ReturnsOutput source '{connection.SourceId}' is not a picking station.");
                    }
                    GeneralLink.CreateLink(pick.GetReturnsPort(), new List<Element> { target });
                    Log($"  Wired {source.GetName()}.ReturnsPort -> {target.GetName()}");
                    break;
                }

                default:
                    throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': '{connection.ConnectionType}' is wired with the source's output link.");
            }
        }

        // Picking / Assembly station material feed: the station's SKU stock port (a destination of the feed's output).
        private Element StockPortOf(ConnectionConfig connection)
        {
            if (!createdElements.TryGetValue(connection.TargetId, out Element target))
                throw new FormatException($"Connection '{connection.SourceId}' -> '{connection.TargetId}': the target is not an element of the model.");
            SkuStockPort port = null;
            if      (target is PickingStation ps)  port = ps.GetStockPort();
            else if (target is AssemblyStation asy) port = asy.GetStockPort();
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
