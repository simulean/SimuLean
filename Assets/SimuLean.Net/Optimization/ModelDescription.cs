using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>
    /// The whole model in one compact, JSON-friendly tree (<c>simulean_model</c>): the canonical model — the
    /// <see cref="SimulationConfig"/> a run builds — rather than the scene, with everything named by name (not by id) and
    /// every parameter under the key a study varies (<c>Owner.key</c>, <c>team:T.key</c>, <c>downtime:id.key</c>,
    /// <c>reaction:id.key</c>): the clock (time unit, horizon, warm-up, end condition, seed), the elements (type, next
    /// elements in order, parameters), the other connections (lists, ports), the teams (members with their labels,
    /// parameters), lists, downtimes, reactions and data tables. Read-only.
    /// <para>Every object a value names is written by its name (a team id
    /// <c>Operators_2569958:768</c>, an automatic point <c>pos:UnitySource_…</c>); a team shows its travel, home node and
    /// request orders, a downtime its repair crew. Each team and downtime carries its <c>metrics</c>: the
    /// metric paths a study reads (<c>team:T.utilization</c>, <c>downtime:Id.stops</c>, one target of several
    /// <c>downtime:Id@&lt;target&gt;.stops</c> — a team target's members by name) and the statistic names once, taken from
    /// <see cref="ResultMetrics"/> / <see cref="MetricCatalog"/>. Compact by default: empty texts are left out and a long or generated
    /// list (a rack's storage lanes) is one summary line per field (<c>lanes[*].capacity = 1</c>); <c>full</c> lists every
    /// value.</para>
    /// </summary>
    public static class ModelDescription
    {
        /// <param name="parameters">Include every element's / team's parameters (false: the structure only).</param>
        /// <param name="only">Describe only the element / team / list / downtime / reaction of this name (null = all).</param>
        /// <param name="full">Every value (empty texts, each item of a long list); false = compact.</param>
        public static Dictionary<string, object> Of(SimulationConfig model, bool parameters = true, string only = null, bool full = false)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in model.Elements) if (e.Id != null) names[e.Id] = e.Name ?? e.Id;
            foreach (var l in model.Lists) if (l.Id != null) names[l.Id] = l.Name ?? l.Id;
            foreach (var t in model.ResourceTeams) if (t.Id != null) names[t.Id] = t.Name ?? t.Id;
            string NameOf(string id) => id != null && names.TryGetValue(id, out var n) ? n : id;
            bool Wanted(string name) => only == null || string.Equals(name, only, StringComparison.Ordinal);
            // ids inside values (a team, a timeout target, a release point) and the scene's automatic points, by name
            var renames = new List<KeyValuePair<string, string>>();
            foreach (var kv in names)
            {
                if (kv.Key.Length == 0) continue;
                renames.Add(new KeyValuePair<string, string>(AutoPoint + kv.Key, kv.Value + " (its position)"));
                renames.Add(new KeyValuePair<string, string>(AutoWork + kv.Key, kv.Value + " (work point)"));
                renames.Add(new KeyValuePair<string, string>(AutoHome + kv.Key, kv.Value + " (its position)"));
                if (kv.Key != kv.Value) renames.Add(kv);
            }
            renames.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            string Named(string text)
            {
                if (string.IsNullOrEmpty(text)) return text;
                foreach (var kv in renames)
                    if (text.Contains(kv.Key)) text = text.Replace(kv.Key, kv.Value);
                return text;
            }
            SortedDictionary<string, string> Shown(SortedDictionary<string, string> p)
            {
                var d2 = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in p)
                {
                    // a point the scene made itself (an element's own position, a team's start) says nothing in a compact view
                    if (!full && kv.Value != null && (kv.Value.StartsWith(AutoPoint, StringComparison.Ordinal) || kv.Value.StartsWith(AutoWork, StringComparison.Ordinal)
                                                      || kv.Value.StartsWith(AutoHome, StringComparison.Ordinal))) continue;
                    d2[kv.Key] = Named(kv.Value);
                }
                return full ? d2 : Compact(d2);
            }

            var d = new Dictionary<string, object>();
            double unit = model.TimeUnitSeconds > 0 ? model.TimeUnitSeconds : 1.0;
            // the clock always (an object's answer lost the time unit and horizon its numbers are in)
            {
                string end = null;
                if (model.StopAt != null && model.StopAt.Count > 0 && model.StopAtCount > 0)
                    end = $"{model.StopAtCount} items received by {string.Join(", ", model.StopAt.Select(NameOf))}";
                if (model.StopWhenEmpty) end = (end != null ? end + "; " : "") + "nothing left to do (or a deadlock / stall)";
                if (model.StopWhenNoProgressFor > 0) end = (end != null ? end + "; " : "") + StudySpec.NoProgressText(model.StopWhenNoProgressFor);
                d["clock"] = new Dictionary<string, object>
                {
                    ["timeUnit"] = ResultMetrics.TimeUnitName(unit),
                    ["secondsPerTimeUnit"] = unit,
                    ["horizon"] = model.MaxSimulationTime,
                    ["warmup"] = model.WarmupTime,
                    ["start"] = string.IsNullOrWhiteSpace(model.StartDateTime) ? null : model.StartDateTime,
                    ["endCondition"] = end,
                    ["seed"] = model.UseFixedSeed ? (object)model.Seed : "per run",
                    ["holidays"] = model.Holidays != null && model.Holidays.Count > 0 ? model.Holidays : null,
                };
            }

            // flow connections in order (index routing reads it): the element's "next"; the others listed apart
            var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var other = new List<string>();
            foreach (var c in model.Connections)
            {
                if (ConnectionConfig.EffectiveType(c) == "General")
                {
                    if (!next.TryGetValue(c.SourceId ?? "", out var list)) next[c.SourceId ?? ""] = list = new List<string>();
                    list.Add(NameOf(c.TargetId));
                }
                else if (only == null || Wanted(NameOf(c.SourceId)) || Wanted(NameOf(c.TargetId)))
                    other.Add($"{NameOf(c.SourceId)} -> {NameOf(c.TargetId)} ({c.ConnectionType}{Extra(c)})");
            }

            d["elements"] = model.Elements.Where(e => Wanted(e.Name)).Select(e =>
            {
                var x = new Dictionary<string, object>
                {
                    ["name"] = e.Name,
                    ["type"] = e.Type,
                    ["next"] = next.TryGetValue(e.Id ?? "", out var n) ? n : new List<string>(),
                };
                if (parameters) x["parameters"] = Shown(Texts(e.Values()));
                return x;
            }).ToList();
            if (other.Count > 0) d["connections"] = other;

            var teams = model.ResourceTeams.Where(t => Wanted(t.Name)).Select(t =>
            {
                var x = new Dictionary<string, object>
                {
                    ["name"] = t.Name,
                    // by the name the built member has (an unnamed one: <Team>_<n>) — its statistics and downtimes carry it
                    ["members"] = t.Members.Select((m, i) =>
                    {
                        string member = ResourceBuild.MemberName(ResourceBuild.TeamName(t), m, i + 1);
                        return m.Properties != null && m.Properties.Count > 0 ? $"{member} [{string.Join(", ", m.Properties)}]" : member;
                    }).ToList(),
                    ["metrics"] = TeamMetricPaths(t),
                };
                if (parameters)
                {
                    var p = Catalog(ModelParameterCatalog.ForTeam(t));
                    // the team's text settings a reader needs: how it travels, where it starts, how it orders its requests
                    p["travel"] = t.Travel.ToString();
                    if (t.Travel != TravelKind.Abstract) p["homeNode"] = string.IsNullOrEmpty(t.HomeNode) ? "(none: the network's first node)" : t.HomeNode;
                    p["requestOrder"] = string.IsNullOrWhiteSpace(t.RequestOrder) ? "(first come, first served)" : t.RequestOrder.Trim();
                    if (!string.IsNullOrWhiteSpace(t.ResourceOrder)) p["resourceOrder"] = t.ResourceOrder.Trim();
                    if (t.NamedOrders != null && t.NamedOrders.Count > 0)
                        p["namedOrders"] = string.Join(", ", t.NamedOrders.Where(o => o != null).Select(o => $"{o.Name} = {o.Query}"));
                    if (!string.IsNullOrEmpty(t.Matrix)) p["matrix"] = t.Matrix;
                    if (!string.IsNullOrEmpty(t.WalkGrid)) p["walkGrid"] = t.WalkGrid;
                    x["parameters"] = Shown(p);
                }
                return x;
            }).ToList();
            if (teams.Count > 0) d["teams"] = teams;

            var lists = model.Lists.Where(l => Wanted(l.Name)).Select(l => new Dictionary<string, object>
            {
                ["name"] = l.Name,
                ["order"] = string.IsNullOrEmpty(l.DefaultOrderBy) ? "push order" : l.DefaultOrderBy + (l.DefaultDescending ? " desc" : ""),
                ["fields"] = l.Fields.Where(f => f != null).Select(f => string.IsNullOrEmpty(f.source) ? $"{f.name} ({f.kind})" : $"{f.name} ({f.kind}: {f.source})").ToList(),
            }).ToList();
            if (lists.Count > 0) d["lists"] = lists;

            var downtimes = model.Downtimes.Where(t => Wanted(t.Id)).Select(t =>
            {
                var x = new Dictionary<string, object>
                {
                    ["id"] = t.Id, ["kind"] = t.Kind, ["targets"] = t.TargetIds.Select(NameOf).ToList(),
                    ["metrics"] = DowntimeMetricPaths(t, model, NameOf),
                };
                if (parameters)
                {
                    var p = Catalog(ModelParameterCatalog.ForDowntime(t));
                    // its repair crew: the team and the requirements are names, not catalog numbers
                    foreach (var (path, value) in t.Values())
                        if (path.StartsWith("repairBy.", StringComparison.Ordinal) && !p.ContainsKey(path) && value is string s && s.Trim().Length > 0)
                            p[path] = s.Trim();
                    // the state a stop puts its targets in: not a catalog key (a text), but what the statistics are read by
                    if (!string.IsNullOrWhiteSpace(t.Settings?.StateName)) p["stateName"] = t.Settings.StateName.Trim();
                    x["parameters"] = Shown(p);
                    var rows = TimetableRows(t, NameOf, full);
                    if (rows != null) x["rows"] = rows;
                }
                return x;
            }).ToList();
            if (downtimes.Count > 0) d["downtimes"] = downtimes;

            var reactions = model.Reactions.Where(r => Wanted(r.Id)).Select(r =>
            {
                var x = new Dictionary<string, object> { ["id"] = r.Id, ["sources"] = r.Sources.Select(NameOf).ToList() };
                if (parameters) x["parameters"] = Shown(Texts(r.Values()));
                return x;
            }).ToList();
            if (reactions.Count > 0) d["reactions"] = reactions;

            var tables = model.Tables.Where(t => Wanted(t.Name)).Select(t => new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["source"] = t.Source == DataSourceKind.File ? "file " + t.File : t.Source == DataSourceKind.Sql ? "sql " + t.Sql : "inline",
                ["columns"] = t.Columns?.Keys.ToList(),
                ["rows"] = t.Columns == null || t.Columns.Count == 0 ? 0 : t.Columns.Values.First().Count,
            }).ToList();
            if (tables.Count > 0) d["tables"] = tables;
            // the transport network: its nodes with their positions, its tracks with their lengths, the teams
            // that travel on it; with only = "network" or one of its nodes, the network alone
            bool networkWanted = only == null || string.Equals(only, NetworkKey, StringComparison.OrdinalIgnoreCase)
                                 || (model.Network?.Nodes.Any(n => n != null && n.Id == only) ?? false);
            var network = networkWanted ? NetworkOf(model, Named, full) : null;
            if (network != null) d[NetworkKey] = network;
            if (only != null && ((IList)d["elements"]).Count == 0 && teams.Count == 0 && lists.Count == 0 && downtimes.Count == 0
                && reactions.Count == 0 && tables.Count == 0 && network == null)
                throw new ArgumentException($"The model has nothing named '{only}'." + NameSuggestions.DidYouMean(only,
                    model.Elements.Select(e => e.Name).Concat(model.ResourceTeams.Select(t => t.Name)).Concat(model.Lists.Select(l => l.Name))
                         .Concat(model.Downtimes.Select(t => t.Id)).Concat(model.Reactions.Select(r => r.Id)).Concat(model.Tables.Select(t => t.Name))));
            // one object asked for that is not an element (a team, a downtime): no empty element list beside it
            if (only != null && ((IList)d["elements"]).Count == 0) d.Remove("elements");
            return d;
        }

        /// <summary>
        /// A Timetable downtime's intervals as one line each — <c>start+duration</c>, then the target (when the row names one)
        /// and the row's state, mode and code when it sets them: <c>480+30 Press STOPPED AfterCurrent</c>. Compact: the first
        /// <see cref="LongList"/> rows and "… n more"; <paramref name="full"/> every row. Null for another kind or no rows.
        /// </summary>
        private static List<string> TimetableRows(DowntimeConfig t, Func<string, string> nameOf, bool full)
        {
            var rows = t?.Settings?.Rows;
            if (rows == null || rows.Count == 0) return null;
            var lines = new List<string>();
            foreach (var r in rows)
            {
                if (r == null) continue;
                if (!full && lines.Count == LongList) { lines.Add($"… {rows.Count - LongList} more (full lists them)"); break; }
                var parts = new List<string> { $"{(r.Start ?? "0").Trim()}+{r.Duration.ToString("G6", CultureInfo.InvariantCulture)}" };
                if (!string.IsNullOrEmpty(r.Target)) parts.Add(nameOf(r.Target));
                if (!string.IsNullOrWhiteSpace(r.State)) parts.Add(r.State.Trim());
                if (r.Mode != null) parts.Add(r.Mode.ToString());
                if (!string.IsNullOrWhiteSpace(r.Code)) parts.Add("code " + r.Code.Trim());
                lines.Add(string.Join(" ", parts));
            }
            return lines;
        }

        // ── the transport network ──────────────────────────

        /// <summary>The key of the network in the description, and the name that asks for it alone (<c>only</c>).</summary>
        public const string NetworkKey = "network";

        /// <summary>Nodes and tracks listed one by one in a compact description; more are counted (<c>full</c> lists all).</summary>
        public const int NetworkShown = 40;

        /// <summary>
        /// The model's transport network, so the network built can be read back: its
        /// <c>nodes</c> as <c>id (x, z)</c> with the elements located there, its <c>tracks</c> as <c>A – B: length</c> (two-way;
        /// <c>A → B</c> one-way; the straight distance when no length is given, its type when not a travel path) and the
        /// <c>teams</c> that use it (Network travel on the tracks; Straight travel in straight lines between the nodes'
        /// positions). Compact: the scene's automatic points (an element's own position, a team's start) are left out and each
        /// list stops after <see cref="NetworkShown"/> items. Null when the model has no node.
        /// </summary>
        private static Dictionary<string, object> NetworkOf(SimulationConfig model, Func<string, string> named, bool full)
        {
            var net = model.Network;
            if (net == null || net.Nodes.Count == 0) return null;
            bool Auto(string id) => id != null && (id.StartsWith(AutoPoint, StringComparison.Ordinal) || id.StartsWith(AutoWork, StringComparison.Ordinal)
                                                   || id.StartsWith(AutoHome, StringComparison.Ordinal));
            var byId = new Dictionary<string, NetworkNodeConfig>(StringComparer.Ordinal);
            foreach (var n in net.Nodes) if (n?.Id != null && !byId.ContainsKey(n.Id)) byId[n.Id] = n;
            var located = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // node → the elements located there
            foreach (var e in model.Elements)
                foreach (var (path, value) in e.Values())
                    if (path == "location" && value is string at && at.Length > 0)
                    {
                        if (!located.TryGetValue(at, out var list)) located[at] = list = new List<string>();
                        list.Add(e.Name ?? e.Id);
                    }
            string Num(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
            var nodes = byId.Values.Where(n => full || !Auto(n.Id))
                .Select(n => $"{named(n.Id)} ({Num(n.X)}, {Num(n.Z)})" + (located.TryGetValue(n.Id, out var els) ? ": " + string.Join(", ", els) : ""))
                .ToList();
            var tracks = net.Tracks.Where(t => t != null).Select(t =>
            {
                double length = t.Length;
                if (length < 0 && byId.TryGetValue(t.From ?? "", out var a) && byId.TryGetValue(t.To ?? "", out var b))
                    length = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
                return $"{named(t.From)} {(t.Bidirectional ? "–" : "→")} {named(t.To)}: {(length >= 0 ? Num(length) : "?")}" +
                       (t.Type != SimuLean.Transport.TrackType.TravelPath ? $" ({t.Type})" : "");
            }).ToList();
            var teams = model.ResourceTeams.Where(t => t != null && (t.Travel == TravelKind.Network || t.Travel == TravelKind.Straight
                                                                     || t.CalibrationBase == TravelKind.Network))
                .Select(t => $"{t.Name ?? t.Id} ({t.Travel}" + (t.Travel == TravelKind.Straight ? ": straight lines between the node positions, not the tracks" : "") +
                             (string.IsNullOrEmpty(t.HomeNode) ? "" : ", home " + named(t.HomeNode)) +
                             $", speed {Num(t.Speed)} {(t.Units == ResourceUnits.Physical ? "m/s" : "per time unit")}" + (t.Traffic ? ", traffic control" : "") + ")")
                .ToList();
            if (!full && nodes.Count == 0 && tracks.Count == 0) return null;   // only the automatic points of Straight travel
            var d = new Dictionary<string, object>();
            object Capped(List<string> items, string what)
                => full || items.Count <= NetworkShown ? (object)items
                   : items.Take(NetworkShown).Concat(new[] { $"… {items.Count - NetworkShown} more {what} (detail=full lists them)" }).ToList();
            d["nodes"] = Capped(nodes, "nodes");
            if (!full && nodes.Count < byId.Count) d["automaticPoints"] = $"{byId.Count - nodes.Count} (the elements' own positions and the teams' starts, for Straight travel: detail=full lists them)";
            bool onTracks = model.ResourceTeams.Any(t => t != null && (t.Travel == TravelKind.Network || t.CalibrationBase == TravelKind.Network));
            d["tracks"] = tracks.Count > 0 ? Capped(tracks, "tracks")
                        : onTracks ? "none (Network travel needs tracks: simulean_connect between two nodes)" : (object)"none";
            d["teams"] = teams;
            if (model.ResourceTeams.Any(t => t != null && t.Traffic)) d["segmentCapacity"] = net.SegmentCapacity;
            return d;
        }

        // ── metric paths ────────────────────────────────

        private const string State = ResultMetrics.StatePrefix + "<STATE>";

        /// <summary>A team's metric paths: <c>team:T.&lt;statistic&gt;</c> with the statistics a team metric reads, and what each
        /// member reports (its run's <c>resources</c> block: a study metric reads the team, not a member).</summary>
        private static Dictionary<string, object> TeamMetricPaths(ResourceTeamConfig t) => new Dictionary<string, object>
        {
            ["path"] = $"team:{ResourceBuild.TeamName(t)}.<statistic>",
            ["statistics"] = string.Join(", ", MetricCatalog.TeamMetricStatistics) + ", " + State,
            ["eachMember"] = string.Join(", ", ResultMetrics.Resource) + ", " + State +
                             " — per member name in a run's resources block (simulean_run_headless detail=full); not a study metric",
        };

        /// <summary>
        /// A downtime's metric paths: <c>downtime:Id.&lt;statistic&gt;</c> (all its targets together) and, with several targets
        /// (one generator each: an element, or each member of a team target), <c>downtime:Id@&lt;target&gt;.&lt;statistic&gt;</c> with
        /// the target names — a team's members by name (<see cref="ResourceBuild.MemberName"/>), as the results name them
        /// (<see cref="RunResults"/>: <c>Id@target</c> only when the downtime has more than one generator).
        /// </summary>
        private static Dictionary<string, object> DowntimeMetricPaths(DowntimeConfig t, SimulationConfig model, Func<string, string> nameOf)
        {
            var targets = new List<string>();
            foreach (string id in t.TargetIds)
            {
                var team = model.ResourceTeams.FirstOrDefault(x => x.Id == id);
                if (team != null && !model.Elements.Any(e => e.Id == id))
                    targets.AddRange(team.Members.Select((m, i) => ResourceBuild.MemberName(ResourceBuild.TeamName(team), m, i + 1)));
                else targets.Add(nameOf(id));
            }
            var statistics = new List<string>(ResultMetrics.Downtime);
            if (t.Settings?.RepairBy != null && ResourceBuild.IsActive(t.Settings.RepairBy.Team, t.Settings.RepairBy.Requirements)) statistics.AddRange(ResultMetrics.Repair);   // only with a repair crew
            var d = new Dictionary<string, object>
            {
                ["path"] = $"downtime:{t.Id}.<statistic>",   // with several targets: all of them together
                ["statistics"] = string.Join(", ", statistics),
            };
            if (targets.Count > 1)
            {
                d["eachTarget"] = $"downtime:{t.Id}@<target>.<statistic>";
                d["targets"] = targets.Count > LongList
                    ? $"{string.Join(", ", targets.Take(3))}, … {targets[targets.Count - 1]} ({targets.Count})"
                    : string.Join(", ", targets);
            }
            return d;
        }

        // the scene's automatic points (SceneExtractor.AutoPointPrefix, the robot work points, ExtractionRules.AutoHomeId)
        private const string AutoPoint = "pos:", AutoWork = "work:", AutoHome = "home:";

        /// <summary>Lists of at least this many items are summarised in a compact description (a rack's lanes always).</summary>
        public const int LongList = 8;

        private static readonly System.Text.RegularExpressions.Regex ListItem =
            new System.Text.RegularExpressions.Regex(@"^(?<list>[A-Za-z_][A-Za-z0-9_.]*)\[(?<i>\d+)\]\.(?<field>.+)$");

        /// <summary>
        /// The compact parameters: empty texts left out; a long list (<see cref="LongList"/> items or more, or a storage's
        /// <c>lanes</c>) as <c>list</c> = "N items" and one <c>list[*].field</c> per field — its value when every item has
        /// the same, else the range of the numbers or the distinct texts.
        /// </summary>
        public static SortedDictionary<string, string> Compact(SortedDictionary<string, string> p)
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var lists = new Dictionary<string, SortedDictionary<int, Dictionary<string, string>>>(StringComparer.Ordinal);
            foreach (var kv in p)
            {
                var m = ListItem.Match(kv.Key);
                if (m.Success)
                {
                    string list = m.Groups["list"].Value;
                    if (!lists.TryGetValue(list, out var items)) lists[list] = items = new SortedDictionary<int, Dictionary<string, string>>();
                    int i = int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture);
                    if (!items.TryGetValue(i, out var fields)) items[i] = fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    fields[m.Groups["field"].Value] = kv.Value;
                    continue;
                }
                if (kv.Value != null && kv.Value.Length > 0) d[kv.Key] = kv.Value;
            }
            foreach (var kv in lists)
            {
                bool summarise = kv.Value.Count >= LongList || (kv.Key == "lanes" && kv.Value.Count > 1);
                if (!summarise)
                {
                    foreach (var item in kv.Value)
                        foreach (var f in item.Value)
                            if (f.Value != null && f.Value.Length > 0) d[$"{kv.Key}[{item.Key}].{f.Key}"] = f.Value;
                    continue;
                }
                d[kv.Key] = $"{kv.Value.Count} items (detail=full lists each)";
                foreach (string field in kv.Value.Values.SelectMany(f => f.Keys).Distinct().OrderBy(f => f, StringComparer.Ordinal))
                {
                    var values = kv.Value.Values.Select(f => f.TryGetValue(field, out string v) ? v ?? "" : "").ToList();
                    var distinct = values.Distinct().ToList();
                    if (distinct.Count == 1 && distinct[0].Length == 0) continue;
                    string text;
                    if (distinct.Count == 1) text = distinct[0];
                    else if (values.All(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                    {
                        var nums = values.Select(v => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
                        text = $"{Short(nums.Min())}..{Short(nums.Max())}";
                    }
                    else if (distinct.Count <= 4) text = string.Join(" | ", distinct);
                    else text = $"{values.First()} … {values.Last()} ({distinct.Count} values)";
                    d[$"{kv.Key}[*].{field}"] = text;
                }
            }
            return d;
        }

        private static string Short(double x) => Math.Round(x, 3).ToString("R", CultureInfo.InvariantCulture);

        private static string Extra(ConnectionConfig c)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(c.Port)) parts.Add("port=" + c.Port);
            if (c.Input != null) parts.Add("input=" + Text(c.Input.Value));
            if (c.Query != null) parts.Add("query=" + c.Query);
            return parts.Count == 0 ? "" : ": " + string.Join(", ", parts);
        }

        private static SortedDictionary<string, string> Texts(Dictionary<string, object> p)
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (p != null) foreach (var kv in p) d[kv.Key] = Text(kv.Value);
            return d;
        }

        private static SortedDictionary<string, string> Texts(IEnumerable<(string path, object value)> settings)
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, value) in settings) d[path] = Text(value);
            return d;
        }

        private static SortedDictionary<string, string> Catalog(IEnumerable<ParameterInfo> list)
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in list) d[p.Key] = p.CurrentText;
            return d;
        }

        /// <summary>A config value as text (invariant numbers; lists in brackets; a nested table as its size).</summary>
        public static string Text(object v)
        {
            switch (v)
            {
                case null: return "";
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case double x: return x.ToString("R", CultureInfo.InvariantCulture);
                case float f: return ((double)f).ToString("R", CultureInfo.InvariantCulture);
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                case IDictionary dd: return $"({dd.Count} entries)";
                case IEnumerable e: return "[" + string.Join(", ", e.Cast<object>().Select(Text)) + "]";
                default: return v.ToString();
            }
        }
    }
}
