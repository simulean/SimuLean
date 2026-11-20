using System.Collections.Generic;
using UnityEngine;
using SimuLean.Serialization;

namespace SimuLean.Unity
{
    /// <summary>
    /// The scene as a model: its <see cref="SimulationConfig"/> plus the ids given to each scene component, so the
    /// engine objects the factory builds can be bound back to their components (<see cref="UnitySimClock"/> scene runs).
    /// </summary>
    public sealed class SceneExtraction
    {
        public SimulationConfig Config { get; } = new SimulationConfig();
        public Dictionary<SElement, string> ElementIds { get; } = new Dictionary<SElement, string>();
        public Dictionary<UnitySimList, string> ListIds { get; } = new Dictionary<UnitySimList, string>();
        public Dictionary<UnityResourceTeam, string> TeamIds { get; } = new Dictionary<UnityResourceTeam, string>();
        public Dictionary<UnityDowntime, string> DowntimeIds { get; } = new Dictionary<UnityDowntime, string>();
        public Dictionary<UnityReaction, string> ReactionIds { get; } = new Dictionary<UnityReaction, string>();

        private Dictionary<string, SElement> byId;

        /// <summary>The scene component of element <paramref name="id"/>, or null.</summary>
        public SElement ElementOf(string id)
        {
            if (byId == null)
            {
                byId = new Dictionary<string, SElement>();
                foreach (var kv in ElementIds) byId[kv.Value] = kv.Key;
            }
            return id != null && byId.TryGetValue(id, out SElement se) ? se : null;
        }
    }

    /// <summary>
    /// Scene → <see cref="SimulationConfig"/>: the single description of a scene's model, used by the scene run, the
    /// headless run, the Experimenter and the Optimizer alike (one build path: the engine handlers build it).
    /// </summary>
    public static class SceneExtractor
    {
        // True when the element's type has the shared settings group (a Sink has no transportBy, a Source no inputStrategy).
        private static bool Has(ElementConfig ec, string group)
            => ec.Settings != null && SettingsPaths.Declares(ec.Settings.GetType(), group);

        private static readonly ElementExtractorRegistry Registry = ElementExtractorRegistry.CreateDefault();

        /// <param name="root">Model root (null = the whole scene).</param>
        /// <param name="sceneRun">True for a run in the scene itself: teams moving with Unity physics are timed by
        /// the physics (external travel); false (headless) uses their calibration.</param>
        /// <summary>The id of the team an element uses; a team that is not part of the model (inactive, or outside the
        /// model root) is a clear error (not a KeyNotFoundException, which the clock would retry every frame).</summary>
        /// <summary>The model id of a team (its name and object id; unique in the scene).</summary>
        public static string TeamId(UnityResourceTeam team) => team.TeamName + "_" + UnityCompat.ObjectId(team);

        private static string TeamIdOf(SceneExtraction x, SElement se, UnityResourceTeam team, string how)
        {
            if (team == null) return "";   // a use whose requirements name their teams
            if (x.TeamIds.TryGetValue(team, out string id)) return id;
            throw new System.InvalidOperationException($"'{se.name}' is {how} by team '{team.name}', which is not part of the model " +
                                                "(inactive, or outside the model root).");
        }

        public static SceneExtraction Extract(GameObject root = null, bool sceneRun = false)
        {
            var x = new SceneExtraction();
            var config = x.Config;

            SElement[] elements = All<SElement>(root);

            // Elements: ids and parameters
            foreach (var element in elements)
            {
                if (element == null) continue;
                string id = ElementId(element);
                x.ElementIds[element] = id;
                var ec = new ElementConfig(id, Registry.EngineTypeOf(element.GetType().Name), element.name);
                ExtractOwn(element, ec);
                // rule parameters that change nothing: a list without its rule, a name the rule lacks
                foreach (string w in RuleParameterChecks.Problems(element, includeTeams: false)) Debug.LogWarning("[SimuLean] " + w, element);
                config.Elements.Add(ec);
            }

            // Data tables: the snapshot every run starts from, and the project database SQL tables read
            config.DatabasePath = ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot);
            foreach (var t in All<UnityDataTable>(root))
            {
                if (t == null) continue;
                config.Tables.Add(t.Snapshot(config.DatabasePath, out string warning));
                if (warning != null) Debug.LogWarning("[SimuLean] " + warning, t);
            }

            // Lists (ids for the PushToList / PullFromList connections)
            foreach (var l in All<UnitySimList>(root))
            {
                string listId = "list_" + l.ListName;
                x.ListIds[l] = listId;
                config.Lists.Add(new ListConfig
                {
                    Id = listId, Name = l.ListName, DefaultOrderBy = l.defaultOrderBy ?? "", DefaultDescending = l.defaultDescending,
                    Fields = l.fields != null ? l.fields.ConvertAll(f => f.Clone()) : new List<SimuLean.Collections.ListFieldSpec>(),
                });
            }

            // Connections
            foreach (var element in elements)
                ExtractConnections(x, element);

            // Parameters naming other elements (their ids are all known now), e.g. a milk run's flows
            foreach (var element in elements)
                if (element != null && Registry.TryGetExtractor(element.GetType().Name, out var rx) && rx is IElementReferenceExtractor refs
                    && x.ElementIds.TryGetValue(element, out string refId))
                    refs.ExtractReferences(element, config.Elements.Find(e => e.Id == refId), x.ElementIds);

            // Combiner input order (ids of the upstream elements; [0] = main input) and list inputs
            foreach (var element in elements)
            {
                if (!(element is UnityCombiner comb) || !x.ElementIds.TryGetValue(comb, out string combId)) continue;
                var combConfig = config.Elements.Find(e => e.Id == combId);
                var order = new List<string>();   // "" = an input fed by a list
                foreach (SElement up in comb.ResolveInputSlots())
                    order.Add(up != null && x.ElementIds.TryGetValue(up, out string upId) ? upId : "");
                combConfig.Set("inputOrder", order);
                if (comb.listInputs != null)
                    foreach (var li in comb.listInputs)
                    {
                        if (li == null || li.list == null || !x.ListIds.TryGetValue(li.list, out string lid)) continue;
                        var conn = new ConnectionConfig(lid, combId, "PullFromList");
                        conn.Query = li.query != null ? li.query.Encode() : "";
                        conn.Input = li.input;
                        config.Connections.Add(conn);
                    }
            }

            // Run settings and calendar of the scene clock
            SimCalendar calendar = SimCalendar.Default;
            var simClock = ClockOf(root);
            if (simClock != null)
            {
                calendar = simClock.CreateCalendar();
                config.StartDateTime     = calendar.IsDefault ? null : simClock.StartDateTime;
                config.TimeUnitSeconds   = calendar.SecondsPerUnit;
                config.Holidays = new List<string>();
                foreach (var h in simClock.Holidays) if (!string.IsNullOrWhiteSpace(h)) config.Holidays.Add(h.Trim());
                config.MaxSimulationTime = simClock.MaxTime;
                config.WarmupTime        = simClock.WarmupTime;
                // End condition: the elements by their ids (names may repeat); those outside the model root are left out.
                config.StopAt.Clear();
                foreach (var e in simClock.StopAt)
                    if (e != null && x.ElementIds.TryGetValue(e, out string endId)) config.StopAt.Add(endId);
                config.StopAtCount   = config.StopAt.Count > 0 ? simClock.StopAtCount : 0;
                config.StopWhenEmpty = simClock.StopWhenEmpty;
                config.StopWhenNoProgressFor = simClock.StopWhenNoProgressFor;
                // Fixed mode → that seed; random each run → the seed the scene used last (UseFixedSeed replays it).
                config.UseFixedSeed = simClock.SeedMode == SeedMode.Fixed;
                if (simClock.SeedMode == SeedMode.Fixed) config.Seed = simClock.Seed;
                else if (simClock.HasLastRunSeed) config.Seed = simClock.LastRunSeed;
            }

            // The transport network (one per model), every other node as a point, and where each element stands
            var networks = All<UnityTransportNetwork>(root);
            if (networks.Length > 1)
                Debug.LogWarning($"[SceneExtractor] {networks.Length} transport networks found: a model has one — '{networks[0].name}' is used.");
            if (networks.Length > 0) config.Network = networks[0].ToConfig();
            var known = new HashSet<string>();
            if (config.Network != null)
                foreach (var rec in config.Network.Nodes) known.Add(rec.Id);
            foreach (var n in All<UnityTransportNode>(root))
            {
                if (!known.Add(n.NodeId)) continue;
                if (config.Network == null) config.Network = new NetworkConfig();
                Vector3 p = n.transform.position;
                config.Network.Nodes.Add(NetworkNodeConfig.Of(n.NodeId, p.x, p.y, p.z));
            }
            foreach (var kv in x.ElementIds)
            {
                if (kv.Key.location != null)
                    config.Elements.Find(e => e.Id == kv.Value).Set("location", kv.Key.location.NodeId);
                var work = kv.Key.workNodes?.FindAll(n => n != null);   // the resources' work points
                if (work != null && work.Count > 0)
                    config.Elements.Find(e => e.Id == kv.Value).Set("workNodes", work.ConvertAll(n => n.NodeId));
            }

            AddStraightTravelPoints(config, x, root);

            // Travel matrices, walk grids, resource teams and each element's use of them
            foreach (var m in All<UnityTravelMatrix>(root))
                config.TravelMatrices.Add(m.ToConfig(SpaceId(m)));
            foreach (var g in All<UnityWalkGrid>(root))
                config.WalkGrids.Add(g.ToConfig(SpaceId(g)));
            foreach (var team in All<UnityResourceTeam>(root))
            {
                string teamId = TeamId(team);
                x.TeamIds[team] = teamId;
                config.ResourceTeams.Add(UnityResourceTeamExtractor.Extract(team, teamId,
                    team.matrix != null ? SpaceId(team.matrix) : null,
                    team.walkGrid != null ? SpaceId(team.walkGrid) : null, sceneRun));
            }
            foreach (var ec in config.Elements)
            {
                SElement se = x.ElementOf(ec.Id);
                if (se == null) continue;
                WarnIdleRequirements(se, se is IProcessResourceUser ? se.processBy : null, "processBy");
                WarnIdleRequirements(se, Has(ec, "transportBy") ? se.transportBy : null, "transportBy");
                if (se is IProcessResourceUser && se.processBy != null && se.processBy.IsUsed)
                    se.processBy.Export(ec, TeamIdOf(x, se, se.processBy.team, "processed"),
                                        other => x.ElementIds.TryGetValue(other, out string otherId) ? otherId : null);
                if (se.transportBy != null && se.transportBy.IsUsed && Has(ec, "transportBy"))
                    se.transportBy.Export(ec, TeamIdOf(x, se, se.transportBy.team, "transported"));
                if (se is ISetupUser su && su.Setup != null && su.Setup.IsUsed && su.Setup.setupBy != null && su.Setup.setupBy.IsUsed)
                    su.Setup.ExportCrew(ec, TeamIdOf(x, se, su.Setup.setupBy.team, "set up"));
            }

            // Downtime generators (targets resolved through the element ids)
            foreach (var downtime in All<UnityDowntime>(root))
            {
                var dc = downtime.ToConfig(se => se != null && x.ElementIds.TryGetValue(se, out string tid) ? tid : null, calendar,
                                           t => t != null && x.TeamIds.TryGetValue(t, out string teamId) ? teamId : null);
                x.DowntimeIds[downtime] = dc.Id;
                config.Downtimes.Add(dc);
            }

            // Reactions: sources and targets resolved through the element ids
            foreach (var reaction in All<UnityReaction>(root))
            {
                var rc = reaction.ToConfig(se => se != null && x.ElementIds.TryGetValue(se, out string rid) ? rid : null,
                                           t => t != null && x.TeamIds.TryGetValue(t, out string tid) ? tid : null);
                x.ReactionIds[reaction] = rc.Id;
                config.Reactions.Add(rc);
            }
            return x;
        }

        // Requirements on a use with no team anywhere (neither the use's nor a requirement's) do nothing — said.
        private static void WarnIdleRequirements(SElement se, ResourceUse use, string key)
        {
            if (use == null || use.IsUsed || use.requirements == null || use.requirements.Count == 0) return;
            Debug.LogWarning($"[SimuLean] '{se.name}': {key} has {use.requirements.Count} requirement(s) but no team (neither " +
                             $"{key}.team nor a requirement's team): it uses no resources. Set {key}.team (the default team), or " +
                             $"each requirement's team.", se);
        }

        /// <summary>The prefix of the point a Straight team goes to at an element without a location.</summary>
        public const string AutoPointPrefix = "pos:";

        /// <summary>The id an element gets in the extracted config (its type, name and object id; unique in the scene).</summary>
        public static string ElementId(SElement element) => $"{element.GetType().Name}_{element.name}_{UnityCompat.ObjectId(element)}";
        /// <summary>The prefix of the work point of a robot team at an element without work nodes.</summary>
        public const string AutoWorkPrefix = "work:";

        /// <summary>
        /// Straight travel: members go in straight lines between points, so every element without a location
        /// gets one — where a person works at it (<see cref="StationModel.StaffPosition"/>: its staff socket, else in front of
        /// it), on its floor — and a team without a home node starts at its own position. Robot arms work at the station's
        /// work socket instead (a work node). Only when a team travels straight: the other models are unchanged.
        /// </summary>
        private static void AddStraightTravelPoints(SimulationConfig config, SceneExtraction x, GameObject root)
        {
            var teams = new List<UnityResourceTeam>();
            foreach (var t in All<UnityResourceTeam>(root)) if (t.travel == TeamTravel.Straight) teams.Add(t);
            if (teams.Count == 0) return;
            if (config.Network == null) config.Network = new NetworkConfig();
            var nodes = config.Network.Nodes;
            void Add(string id, Vector3 p) => nodes.Add(NetworkNodeConfig.Of(id, p.x, p.y, p.z));
            foreach (var team in teams)
                if (team.homeNode == null)
                    Add(ExtractionRules.AutoHomeId(team.TeamName + "_" + UnityCompat.ObjectId(team)), team.transform.position);
            foreach (var kv in x.ElementIds)
            {
                SElement se = kv.Key;
                var ec = config.Elements.Find(e => e.Id == kv.Value);
                if (ec == null) continue;
                if (se.location == null)
                {
                    Add(AutoPointPrefix + kv.Value, UnityResourceTeam.AutoPoint(se));
                    ec.Set("location", AutoPointPrefix + kv.Value);
                }
                // a storage moved by a straight team: each lane without a node at its own position
                if (se is UnityStorageSystem storage && storage.AutoLaneNodes)
                    foreach (var lane in storage.ResolveLanes())
                        if (lane.NodeId != null && lane.NodeId.StartsWith("lane:")) Add(lane.NodeId, lane.Position);
                bool noWorkNodes = se.workNodes == null || !se.workNodes.Exists(n => n != null);
                if (noWorkNodes && ArmsWorkHere(se))
                {
                    var socket = StationModel.Position(se, StationModel.SocketNames);
                    if (socket == null) continue;
                    Add(AutoWorkPrefix + kv.Value, socket.position);
                    ec.Set("workNodes", new List<string> { AutoWorkPrefix + kv.Value });
                }
            }
        }

        // Straight-travel robot arms work at the station's socket: the teams that work at it — the process team, its
        // requirements' teams and the setup crew (not only the process team) — when at least one
        // is an arm team and every other spatial one is too (a walking team keeps the station's location).
        private static bool ArmsWorkHere(SElement se)
        {
            var teams = new List<UnityResourceTeam>();
            if (se.processBy != null && se.processBy.IsUsed)
            {
                if (se.processBy.team != null) teams.Add(se.processBy.team);
                if (se.processBy.requirements != null)
                    foreach (var r in se.processBy.requirements) if (r?.team != null) teams.Add(r.team);
            }
            if (se is ISetupUser su && su.Setup != null && su.Setup.IsUsed && su.Setup.setupBy != null && su.Setup.setupBy.IsUsed)
            {
                teams.Add(su.Setup.setupBy.team);   // null skipped below
                foreach (var r in su.Setup.setupBy.requirements ?? new List<ResourceRequirementSpec>())
                    if (r?.team != null) teams.Add(r.team);
            }
            bool arm = false;
            foreach (var t in teams)
            {
                if (t == null || t.travel == TeamTravel.Abstract) continue;
                if (t.travel == TeamTravel.Straight && t.MembersAreArms) arm = true;
                else return false;
            }
            return arm;
        }

        /// <summary>One element's config (type and parameters, without connections): what an editor shows, e.g. the
        /// optimisable keys of <see cref="SimuLean.Optimization.ModelParameterCatalog.ForElement"/>.</summary>
        public static ElementConfig ExtractElement(SElement element)
        {
            if (element == null) return null;
            var ec = new ElementConfig(element.name, Registry.EngineTypeOf(element.GetType().Name), element.name);
            ExtractOwn(element, ec);
            // its use of resources (the team by name: an editor's view, no ids)
            if (element is IProcessResourceUser && element.processBy != null && element.processBy.IsUsed)
                element.processBy.Export(ec, element.processBy.team != null ? element.processBy.team.TeamName : "");
            if (element.transportBy != null && element.transportBy.IsUsed && Has(ec, "transportBy"))
                element.transportBy.Export(ec, element.transportBy.team != null ? element.transportBy.team.TeamName : "");
            if (element is ISetupUser su && su.Setup != null && su.Setup.setupBy != null && su.Setup.setupBy.IsUsed)
                su.Setup.ExportCrew(ec, su.Setup.setupBy.team != null ? su.Setup.setupBy.team.TeamName : "");
            return ec;
        }

        /// <summary>An element's own parameters: its extractor's, its user properties, routing and admission.</summary>
        private static void ExtractOwn(SElement element, ElementConfig ec)
        {
            string typeName = element.GetType().Name;
            if (Registry.TryGetExtractor(typeName, out var extractor))
                extractor.ExtractParameters(element, ec);
            else
                Debug.LogWarning($"[SceneExtractor] No extractor for '{typeName}' ({element.name}) — parameters skipped.");
            // an element stands upright: a tilt (a Z-up model's rotation left on the element instead of its model child)
            // draws its items and its look on edge (patients standing on a station's edge, 0.6 m up)
            Vector3 e = element.transform.eulerAngles;
            if (Mathf.Abs(Mathf.DeltaAngle(e.x, 0f)) > 1f || Mathf.Abs(Mathf.DeltaAngle(e.z, 0f)) > 1f)
                Debug.LogWarning($"[SimuLean] '{element.name}' is tilted (rotation {e.x:0}, {e.y:0}, {e.z:0}): its items and look are " +
                                 $"drawn on edge. Set its rotation to (0, {e.y:0}, 0); a model's own rotation belongs on its model.", element);
            // Its items walk by themselves (a grid / matrix by the ids the model's spaces get below).
            if (Has(ec, "walkBy")) element.walkBy?.Export(ec, SpaceId);
            // Its pull's max wait (only when it pulls from a list and a limit is set)
            if (element.PullList != null && !string.IsNullOrWhiteSpace(element.pullMaxWait) && Has(ec, "pullMaxWait"))
                ec.Set("pullMaxWait", element.pullMaxWait.Trim());
            // User properties (SElementProperties): numbers, texts, references to other elements by name.
            var userProps = element.GetComponent<SElementProperties>();
            if (userProps != null && userProps.Count > 0) ec.Set(PropertyRecords.Key, userProps.Records());
            // Routing and admission, common to every element.
            if (element.HasOutput && Has(ec, "outputStrategy"))
                ExtractorHelpers.ExtractOutputStrategyConfig(element.outputStrategy, ec);
            if (element.HasInput && element.inputStrategy != null && Has(ec, "inputStrategy"))
                ExtractorHelpers.ExtractInputStrategyConfig(element.inputStrategy, ec);
            // Labels set on hand-over
            if (element.HasInput && HasRecords(element.entryLabels))
                ec.Set("entryLabels", new List<string>(element.entryLabels));
            if (element.HasOutput && HasRecords(element.exitLabels))
                ec.Set("exitLabels", new List<string>(element.exitLabels));
            // Item statistics
            if (element.HasInput && HasRecords(element.tallies))
            {
                ec.Set("tallies", new List<string>(element.tallies));
                if (!string.IsNullOrWhiteSpace(element.tallySplit)) ec.Set("tallySplit", element.tallySplit.Trim());
            }
        }

        private static bool HasRecords(List<string> records)
        {
            if (records == null) return false;
            foreach (var r in records) if (!string.IsNullOrWhiteSpace(r)) return true;
            return false;
        }

        private static string SpaceId(Component c) => c.name + "_" + UnityCompat.ObjectId(c);

        /// <summary>The clock of the model: under the root, else in the root's scene (with several scenes open, another
        /// scene's clock is not this model's), else in the active scene, else any.</summary>
        private static UnitySimClock ClockOf(GameObject root)
        {
            if (root != null && root.GetComponentInChildren<UnitySimClock>() is UnitySimClock own) return own;
            var scene = root != null ? root.scene : UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnitySimClock any = null;
            foreach (var c in UnityCompat.FindAll<UnitySimClock>())
            {
                if (c.gameObject.scene == scene) return c;
                any = any ?? c;
            }
            return any;
        }

        // hierarchy order with or without a root, so the build order (which breaks ties between simultaneous events) is
        // stable across saves and the same for a scene run and a headless run of the same model
        private static T[] All<T>(GameObject root) where T : Component
            => root != null ? root.GetComponentsInChildren<T>() : UnityCompat.FindAllInHierarchyOrder<T>();

        private static void ExtractConnections(SceneExtraction x, SElement element)
        {
            if (element == null || !x.ElementIds.TryGetValue(element, out string sourceId)) return;
            var connections = x.Config.Connections;

            // Lists: pulling, and pushing (which replaces the element's Next Elements)
            if (element.PullList != null && x.ListIds.TryGetValue(element.PullList, out string pullId))
            {
                var conn = new ConnectionConfig(pullId, sourceId, "PullFromList");
                conn.Query = element.pullQuery != null ? element.pullQuery.Encode() : "";
                connections.Add(conn);
            }
            bool pushes = element.PushList != null && x.ListIds.TryGetValue(element.PushList, out string pushId);
            if (pushes)
                connections.Add(new ConnectionConfig(sourceId, x.ListIds[element.PushList], "PushToList"));
            else if (element.nextElements != null)
            {
                if (element.nextElements.Exists(n => n == null))   // a deleted element: the indexes shift
                    Debug.LogWarning($"[SimuLean] '{element.name}' has an empty Next Elements entry (a deleted element?): it is " +
                                     "skipped, so the outputs after it move up one index (index-based routing). Remove it.", element);
                foreach (SElement next in element.nextElements)
                    if (next != null && x.ElementIds.TryGetValue(next, out string nextId))
                        connections.Add(new ConnectionConfig(sourceId, nextId, "General"));
            }

            // Element-specific connections (e.g. station material sources)
            if (Registry.TryGetExtractor(element.GetType().Name, out var extractor))
                extractor.ExtractConnections(element, sourceId, connections, x.ElementIds);
        }
    }
}
