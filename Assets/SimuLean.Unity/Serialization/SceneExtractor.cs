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
        private static readonly ElementExtractorRegistry Registry = ElementExtractorRegistry.CreateDefault();

        /// <param name="root">Model root (null = the whole scene).</param>
        /// <param name="sceneRun">True for a run in the scene itself: teams moving with Unity physics are timed by
        /// the physics (external travel); false (headless) uses their calibration.</param>
        /// <summary>The id of the team an element uses; a team that is not part of the model (inactive, or outside the
        /// model root) is a clear error (it was a KeyNotFoundException, retried every frame by the clock).</summary>
        private static string TeamIdOf(SceneExtraction x, SElement se, UnityResourceTeam team, string how)
        {
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
                string typeName = element.GetType().Name;
                string id = $"{typeName}_{element.name}_{UnityCompat.ObjectId(element)}";
                x.ElementIds[element] = id;
                var ec = new ElementConfig(id, typeName, element.name);
                ExtractOwn(element, ec);
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

            // Combiner input order (ids of the upstream elements; [0] = main input) and list inputs
            foreach (var element in elements)
            {
                if (!(element is UnityCombiner comb) || !x.ElementIds.TryGetValue(comb, out string combId)) continue;
                var combConfig = config.Elements.Find(e => e.Id == combId);
                var order = new List<string>();   // "" = an input fed by a list
                foreach (SElement up in comb.ResolveInputSlots())
                    order.Add(up != null && x.ElementIds.TryGetValue(up, out string upId) ? upId : "");
                combConfig.Parameters["inputOrder"] = order;
                if (comb.listInputs != null)
                    foreach (var li in comb.listInputs)
                    {
                        if (li == null || li.list == null || !x.ListIds.TryGetValue(li.list, out string lid)) continue;
                        var conn = new ConnectionConfig(lid, combId, "PullFromList");
                        conn.Parameters["query"] = li.query != null ? li.query.Encode() : "";
                        conn.Parameters["port"] = li.input;
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
                // End condition (S2): the elements by their ids (names may repeat); those outside the model root are left out.
                config.StopAt.Clear();
                foreach (var e in simClock.StopAt)
                    if (e != null && x.ElementIds.TryGetValue(e, out string endId)) config.StopAt.Add(endId);
                config.StopAtCount   = config.StopAt.Count > 0 ? simClock.StopAtCount : 0;
                config.StopWhenEmpty = simClock.StopWhenEmpty;
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
                foreach (var rec in config.Network.Nodes) known.Add(rec.Split('|')[0]);
            foreach (var n in All<UnityTransportNode>(root))
            {
                if (!known.Add(n.NodeId)) continue;
                if (config.Network == null) config.Network = new NetworkConfig();
                Vector3 p = n.transform.position;
                config.Network.Nodes.Add(TransportStorageConfigCodec.FormatNode(n.NodeId, p.x, p.y, p.z));
            }
            foreach (var kv in x.ElementIds)
            {
                if (kv.Key.location != null)
                    config.Elements.Find(e => e.Id == kv.Value).Parameters[SimuLean.Headless.HeadlessModelFactory.LocationKey] = kv.Key.location.NodeId;
                var work = kv.Key.workNodes?.FindAll(n => n != null);   // the resources' work points (S4)
                if (work != null && work.Count > 0)
                    config.Elements.Find(e => e.Id == kv.Value).Parameters[SimuLean.Headless.HeadlessModelFactory.WorkNodesKey] = work.ConvertAll(n => n.NodeId);
            }

            // Travel matrices, walk grids, resource teams and each element's use of them
            foreach (var m in All<UnityTravelMatrix>(root))
                config.TravelMatrices.Add(m.ToConfig(SpaceId(m)));
            foreach (var g in All<UnityWalkGrid>(root))
                config.WalkGrids.Add(g.ToConfig(SpaceId(g)));
            foreach (var team in All<UnityResourceTeam>(root))
            {
                string teamId = team.TeamName + "_" + UnityCompat.ObjectId(team);
                x.TeamIds[team] = teamId;
                config.ResourceTeams.Add(team.ToConfig(teamId,
                    team.matrix != null ? SpaceId(team.matrix) : null,
                    team.walkGrid != null ? SpaceId(team.walkGrid) : null, sceneRun));
            }
            foreach (var ec in config.Elements)
            {
                SElement se = x.ElementOf(ec.Id);
                if (se == null) continue;
                if (se is IProcessResourceUser && se.processBy != null && se.processBy.IsUsed)
                    se.processBy.Export(ec, TeamIdOf(x, se, se.processBy.team, "processed"),
                                        other => x.ElementIds.TryGetValue(other, out string otherId) ? otherId : null);
                if (se.transportBy != null && se.transportBy.IsUsed)
                    se.transportBy.Export(ec, TeamIdOf(x, se, se.transportBy.team, "transported"));
            }

            // Downtime generators (targets resolved through the element ids)
            foreach (var downtime in All<UnityDowntime>(root))
            {
                var dc = downtime.ToConfig(se => se != null && x.ElementIds.TryGetValue(se, out string tid) ? tid : null, calendar);
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

        /// <summary>One element's config (type and parameters, without connections): what an editor shows, e.g. the
        /// optimisable keys of <see cref="SimuLean.Optimization.ModelParameterCatalog.ForElement"/>.</summary>
        public static ElementConfig ExtractElement(SElement element)
        {
            if (element == null) return null;
            var ec = new ElementConfig(element.name, element.GetType().Name, element.name);
            ExtractOwn(element, ec);
            // its use of resources (the team by name: an editor's view, no ids)
            if (element is IProcessResourceUser && element.processBy != null && element.processBy.IsUsed)
                element.processBy.Export(ec, element.processBy.team != null ? element.processBy.team.TeamName : "");
            if (element.transportBy != null && element.transportBy.IsUsed)
                element.transportBy.Export(ec, element.transportBy.team != null ? element.transportBy.team.TeamName : "");
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
            element.walkBy?.Export(ec, SpaceId);
            // User properties (SElementLabels): numbers, texts, references to other elements by name.
            var userProps = element.GetComponent<SElementLabels>();
            if (userProps != null && userProps.Count > 0) ec.Parameters[PropertyRecords.Key] = userProps.Records();
            // Routing (the shared asset overrides the inline strategy) and admission, common to every element.
            if (element.HasOutput)
                ExtractorHelpers.ExtractOutputStrategyConfig(
                    element.outputStrategyAsset != null ? element.outputStrategyAsset.config : element.outputStrategy, ec);
            if (element.HasInput && element.inputStrategy != null)
                ExtractorHelpers.ExtractInputStrategyConfig(element.inputStrategy, ec);
            // Labels set on hand-over
            if (element.HasInput && HasRecords(element.entryLabels))
                ec.Parameters[ElementConfigKeys.EntryLabels] = new List<string>(element.entryLabels);
            if (element.HasOutput && HasRecords(element.exitLabels))
                ec.Parameters[ElementConfigKeys.ExitLabels] = new List<string>(element.exitLabels);
            // Item statistics
            if (element.HasInput && HasRecords(element.tallies))
            {
                ec.Parameters[ElementConfigKeys.Tallies] = new List<string>(element.tallies);
                if (!string.IsNullOrWhiteSpace(element.tallySplit)) ec.Parameters[ElementConfigKeys.TallySplit] = element.tallySplit.Trim();
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
                conn.Parameters["query"] = element.pullQuery != null ? element.pullQuery.Encode() : "";
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
