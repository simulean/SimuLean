using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Serialization;

namespace SimuLean.Logic
{
    /// <summary>
    /// One decision of an owner in plain language (the logic viewer): the decision sites
    /// that decide one thing together — a routing's strategy, its score and what to do when the best output is full; a Queue's
    /// offer order, sort, rule and gate; a wait's limit, action and target — as one sentence, <see cref="Verb"/> then
    /// <see cref="Text"/> (<c>Route best score: -out.content · if best is full: skip</c>), with what they read, write and name
    /// (<see cref="Links"/>), the raw settings (<see cref="Settings"/>) and, from a live model, the run-time counters
    /// (<see cref="Live"/>, <see cref="ModelLogic.Live"/>).
    /// </summary>
    public sealed class Decision
    {
        /// <summary>The verbs (<see cref="Verb"/>) that name the kind of a decision.</summary>
        public const string VerbRoute = "Route", VerbPush = "Push", VerbAdmit = "Admit", VerbPull = "Pull", VerbRelease = "Release order",
                            VerbOrder = "Order", VerbField = "Field", VerbRule = "Rule", VerbWait = "Wait", VerbResources = "Resources",
                            VerbDispatch = "Dispatch", VerbPlace = "Place", VerbReorder = "Reorder", VerbLabels = "Labels", VerbWhen = "When",
                            VerbSetup = "Setup";

        /// <summary>The site kind (<c>routing</c>, <c>admission</c>, <c>order</c>, <c>dispatch</c>, <c>resources</c>, <c>wait</c>,
        /// <c>labels</c>, <c>reaction</c>…): the colour of its chip.</summary>
        public string Site;
        /// <summary>What it does, one word or two: <c>Route</c>, <c>Admit</c>, <c>Release order</c>, <c>Rule</c> (a choice rule
        /// decides it), <c>Wait</c>, <c>Labels</c>, <c>When</c> (a reaction)…</summary>
        public string Verb;
        /// <summary>The rest of the sentence: <c>best score: -out.content · if best is full: skip</c>.</summary>
        public string Text;
        /// <summary>Every site in it is at its default (first available, first in first out…).</summary>
        public bool IsDefault;
        /// <summary>The decision sites it groups (their raw <c>key = value</c>: <see cref="DecisionSite.Setting"/>).</summary>
        public List<DecisionSite> Sites = new List<DecisionSite>();
        /// <summary>What it reads, writes; its text does not parse; the choice rule that decides it.</summary>
        public List<string> Reads = new List<string>(), Writes = new List<string>();
        /// <summary>It changes only what is seen (a reaction of looks only).</summary>
        public bool ViewOnly;
        /// <summary>The first problem of its sites (a text that does not parse), else null.</summary>
        public string Problem;
        /// <summary>The choice rule that decides it, else null.</summary>
        public string Rule;
        /// <summary>The model objects it names — elements read, lists, teams, the reactions that wake it — by name.</summary>
        public List<string> Links = new List<string>();
        /// <summary>The run-time counters of a live model (<see cref="ModelLogic.Live"/>), null without one.</summary>
        public Dictionary<string, object> Live;
        /// <summary>The counters as one short text (<c>routed Cell A 12, Cell B 30 · last → Cell B (10 vs -2, -1)</c>), null
        /// without them.</summary>
        public string LiveText;

        /// <summary>The sentence: <see cref="Verb"/> then <see cref="Text"/>.</summary>
        public string Sentence => string.IsNullOrEmpty(Text) ? Verb : Verb + " " + Text;

        /// <summary>The raw settings, one <c>key = value</c> each (the non-default ones, or all when every one is default).</summary>
        public List<string> Settings
        {
            get
            {
                var own = Sites.Where(s => !s.IsDefault).ToList();
                return (own.Count > 0 ? own : Sites).Select(s => s.Setting).ToList();
            }
        }

        /// <summary>As plain data: <c>site</c>, <c>say</c> (the sentence), <c>default</c>; a decision of its own also the raw
        /// <c>settings</c>, <c>reads</c>, <c>writes</c>, <c>viewOnly</c>, <c>rule</c>, <c>problem</c>; <c>live</c> with a live
        /// model's counters.</summary>
        public Dictionary<string, object> ToData()
        {
            var d = new Dictionary<string, object> { ["site"] = Site, ["say"] = Sentence };
            if (IsDefault) d["default"] = true;
            else
            {
                d["settings"] = Settings;
                if (Reads.Count > 0) d["reads"] = Reads;
                if (Writes.Count > 0) d["writes"] = Writes;
                if (ViewOnly) d["viewOnly"] = true;
                if (Rule != null) d["rule"] = Rule;
            }
            if (Problem != null) d["problem"] = Problem;
            if (Live != null) d["live"] = Live;
            return d;
        }

        public override string ToString() => Sentence;
    }

    public static partial class ModelLogic
    {
        // ── decisions in plain language ──────────────────────────────────

        // the sites of an owner grouped into decisions, each said as a sentence
        private static void Decide(LogicOwner o)
        {
            o.Decisions.Clear();
            if (o.Kind == "reaction") { if (o.Sites.Count > 0) o.Decisions.Add(ReactionDecision(o)); return; }
            var groups = new List<(string site, string group, List<DecisionSite> sites)>();
            foreach (var s in o.Sites)
            {
                string g = GroupOf(s);
                int k = groups.FindIndex(x => x.site == s.Site && x.group == g);
                if (k < 0) groups.Add((s.Site, g, new List<DecisionSite> { s }));
                else groups[k].sites.Add(s);
            }
            foreach (var (site, group, sites) in groups) o.Decisions.Add(Say(o, site, group, sites));
        }

        // what decides together: a group's settings (outputStrategy.*, processBy.*, tasks[0].*), an element's top-level ones of
        // one site (offerOrder, offerSort…); each label record list, list field, pull and push stands alone
        private static string GroupOf(DecisionSite s)
        {
            string k = s.Key;
            if (k.Contains(":") || s.Site == Name(LogicSite.Labels) || k.StartsWith("fields.", StringComparison.Ordinal)) return k;
            int dot = k.IndexOf('.');
            return dot < 0 ? "" : k.Substring(0, dot);
        }

        private static Decision Say(LogicOwner o, string site, string group, List<DecisionSite> sites)
        {
            var d = new Decision { Site = site, Sites = sites, IsDefault = sites.All(s => s.IsDefault) };
            foreach (var s in sites)
            {
                foreach (string r in s.Reads) if (!d.Reads.Contains(r)) d.Reads.Add(r);
                foreach (string w in s.Writes) if (!d.Writes.Contains(w)) d.Writes.Add(w);
                foreach (string n in s.Refs) if (n != o.Name && !d.Links.Contains(n)) d.Links.Add(n);
                d.Problem = d.Problem ?? s.Problem;
                d.Rule = d.Rule ?? s.Rule;
            }
            string P(string name) => group.Length == 0 ? name : group + "." + name;
            DecisionSite Get(string name) => sites.FirstOrDefault(s => s.Key == P(name));
            var used = new HashSet<DecisionSite>();
            DecisionSite Use(string name) { var s = Get(name); if (s != null) used.Add(s); return s; }
            // the sites not said yet, as key = value (the non-default ones)
            string Rest()
            {
                var rest = sites.Where(s => !used.Contains(s) && !s.IsDefault).Select(s => Short(s.Key, group) + " = " + s.Value).ToList();
                return rest.Count == 0 ? "" : string.Join(" · ", rest);
            }
            string Join(params string[] parts) => string.Join(" · ", parts.Where(x => !string.IsNullOrEmpty(x)));

            if (site == Name(LogicSite.Routing) && group == "outputStrategy")
            {
                // the strategy the builder builds from the text
                var strategy = BuilderChoices.Routing(Use("strategyType")?.Value);
                string full = Use("whenBestFull") is DecisionSite f ? "if best is full: " + f.Value.ToLowerInvariant() : null;
                var rule = Use("rule");
                d.Verb = Decision.VerbRoute;
                switch (strategy)
                {
                    case HeadlessOutputStrategyType.ShortestQueue: d.Text = "to the shortest queue"; break;
                    case HeadlessOutputStrategyType.RoundRobin: d.Text = "in turn (round robin)"; break;
                    case HeadlessOutputStrategyType.MostAvailableCapacity: d.Text = "to the most free room"; break;
                    case HeadlessOutputStrategyType.PriorityBased: d.Text = "by the item's priority"; break;
                    case HeadlessOutputStrategyType.Parameterized: d.Text = Use("parameterKey") is DecisionSite k ? $"by item.{k.Value.Trim()}" : "first available (no label given)"; break;
                    case HeadlessOutputStrategyType.Expression: d.Text = Use("routingExpression") is DecisionSite e ? "to output number " + e.Value : "first available (no expression)"; break;
                    case HeadlessOutputStrategyType.Probability: d.Text = Use("weights") is DecisionSite w ? "by weights " + w.Value.Trim('[', ']') : "by weights (none given)"; break;
                    case HeadlessOutputStrategyType.BestScore: d.Text = Join("best score: " + (Use("scoreExpression")?.Value ?? "?"), full); break;
                    case HeadlessOutputStrategyType.Rule:
                        d.Verb = Decision.VerbRule;
                        d.Text = Join(RuleName(rule) + " picks the next element", full);
                        break;
                    default: d.Text = "first available next element"; break;
                }
                d.Text = Join(d.Text, Rest());
                return d;
            }
            if (sites.Count == 1 && sites[0].Key.StartsWith("push:", StringComparison.Ordinal))
            {
                d.Verb = Decision.VerbPush;
                d.Text = "every item to list " + sites[0].Key.Substring(5);
                return d;
            }
            if (sites.Count == 1 && sites[0].Key.StartsWith("pull:", StringComparison.Ordinal))
            {
                d.Verb = Decision.VerbPull;
                string list = sites[0].Key.Substring(5);
                d.Text = sites[0].IsDefault ? $"from list {list} in its order" : $"from list {list} " + QueryText(sites[0].Value.Contains("|") ? sites[0].Value : sites[0].Value + "|");
                return d;
            }
            if (site == Name(LogicSite.Admission))
            {
                d.Verb = Decision.VerbAdmit;
                string modeKey = group == "inputStrategy" ? "strategyType" : Get("pullModeType") != null ? "pullModeType" : null;
                if (modeKey != null)
                {
                    // an admission as the builder reads it; a Combiner's pull mode is built by its exact name
                    string mode = Use(modeKey) is DecisionSite m && !m.IsDefault
                        ? (modeKey == "strategyType" ? BuilderChoices.Admission(m.Value).ToString() : m.Value) : "Default";
                    string who = modeKey == "pullModeType" ? "components" : "items";
                    switch (mode)
                    {
                        case nameof(HeadlessInputStrategyType.SingleLabel):
                            string label = (Use("singleLabelName")?.Value ?? "?").Trim();
                            d.Text = Use("singleLabelValue") is DecisionSite lv ? $"{who} with item.{label} = {lv.Value}" : $"{who} whose item.{label} matches the main item's";
                            break;
                        case nameof(HeadlessInputStrategyType.MultiLabel): d.Text = $"{who} with any of {Use("multiLabelPairs")?.Value ?? "(none)"}"; break;
                        case nameof(HeadlessInputStrategyType.Condition): d.Text = "when " + (Use("condition")?.Value ?? "1"); break;
                        default: d.Text = "every " + (who == "items" ? "item" : "component"); break;
                    }
                    if (mode == nameof(HeadlessInputStrategyType.Condition) && o.WokenByReactions.Count > 0)
                    {
                        d.Text = Join(d.Text, "woken by " + string.Join(", ", o.WokenByReactions));
                        foreach (string r in o.WokenByReactions) if (!d.Links.Contains(r)) d.Links.Add(r);
                    }
                    d.Text = Join(d.Text, Rest());
                    return d;
                }
                d.Text = Rest();
                if (d.Text.Length == 0) d.Text = Join(sites.Select(s => Short(s.Key, group) + " = " + s.Value).ToArray());
                return d;
            }
            if (site == Name(LogicSite.Order) && o.Kind == "list")
            {
                var s = sites[0];
                if (s.Key.StartsWith("fields.", StringComparison.Ordinal)) { d.Verb = Decision.VerbField; d.Text = s.Key.Substring(7) + " = " + s.Value; }
                else { d.Verb = Decision.VerbOrder; d.Text = s.Value; }
                return d;
            }
            if (site == Name(LogicSite.Order) && (Get("offerOrder") != null || Get("offerSort") != null || Get("offerRule") != null || Get("gated") != null))
            {
                d.Verb = Decision.VerbRelease;
                var order = Use("offerOrder");
                var sort = Use("offerSort");
                var rule = Use("offerRule");
                string main = rule != null ? null : sort != null ? sort.Value
                            : order == null || order.IsDefault ? "first in, first out" : order.Value;
                if (rule != null) { d.Verb = Decision.VerbRule; main = RuleName(rule) + " picks the item to release"; }
                bool anyAccepted = order != null && !order.IsDefault && order.Value == "AnyAccepted";
                if (anyAccepted && main == order.Value) main = "oldest first";
                string any = anyAccepted ? "any item a receiver takes" : null;
                string scan = Use("offerScan") is DecisionSite sc && !sc.IsDefault ? $"looks at {sc.Value} items" : null;
                string gated = Use("gated") is DecisionSite g && !g.IsDefault ? "gated (released by reactions)" : null;
                string grouped = Use("groupBySetup") is DecisionSite gs && !gs.IsDefault
                    ? "grouped by the receiver's setup" + (Use("groupLimit") is DecisionSite gl && !gl.IsDefault ? $" (at most {gl.Value} ahead of the oldest)" : "") : null;
                d.Text = Join(main, any, scan, gated, grouped, Rest());
                return d;
            }
            if (site == Name(LogicSite.Wait))
            {
                d.Verb = Decision.VerbWait;
                var max = Use("maxWait") ?? Use("pullMaxWait");
                string what = max != null && max.Key.EndsWith("pullMaxWait", StringComparison.Ordinal) ? "for a list entry "
                            : group.Length > 0 ? "for resources " : "";
                string action = Use("onTimeout") is DecisionSite t
                    ? (!WaitLimit.TryActionOf(t.Value, out var a) || a == TimeoutAction.KeepWaiting ? "then keep waiting"
                       : a == TimeoutAction.GiveUp ? "then give up" : "then leave") : null;
                string target = Use("timeoutTarget")?.Value?.Trim();
                if (target != null && !d.Links.Contains(target)) d.Links.Add(target);
                if (target != null) target = "→ " + target;
                d.Text = Join(max != null ? what + "at most " + max.Value : null, action == null ? null : action + (target != null ? " " + target : ""), Rest());
                return d;
            }
            if (site == Name(LogicSite.Dispatch) && o.Kind == "team")
            {
                d.Verb = Decision.VerbDispatch;
                var req = Use("requestOrder");
                var mem = Use("resourceOrder");
                Use("namedOrders");   // the orders it may choose from: the chosen one is said
                string requests = req == null ? null : req.Rule != null ? RuleName(req) + " orders the requests" : "requests " + QueryText(req.Value);
                if (req?.Rule != null) d.Verb = Decision.VerbRule;
                d.Text = Join(requests, mem == null ? null : "members " + QueryText(mem.Value), Rest());
                return d;
            }
            if (site == Name(LogicSite.Resources) && group == "setup")
            {
                // the station's setup rule: when, the label, the first item, the time / matrix, then its crew
                d.Verb = Decision.VerbSetup;
                var when = Use("when");
                string rule = when == null ? null : when.Value == "EachItem" ? "before each item"
                            : when.Value == "LabelChange" ? "when item." + (Use("label")?.Value ?? "type").Trim() + " changes" : when.Value;
                string first = Use("setupFirst") is DecisionSite f && when != null ? (f.Value.Trim().ToLowerInvariant() == "false" ? "not for a slot's first item" : null) : null;
                var matrix = Use("matrix");
                var time = Use("time");
                string times = matrix != null ? $"time from matrix '{matrix.Value.Trim()}'" + (time != null ? $" (pairs it lacks: {time.Value})" : "")
                             : time != null ? "time " + time.Value : when != null ? "time 0" : null;
                string keep = Use("keepForProcess") is DecisionSite k && k.Value.Trim().ToLowerInvariant() == "true" ? "the crew stays for the process" : null;
                string crewTeam = Use("setupBy.team")?.Value;
                var crewReq = Use("setupBy.requirements");
                string crew = crewTeam != null || crewReq != null ? "crew: " + (crewTeam != null ? "team " + crewTeam : "") + (crewReq != null ? (crewTeam != null ? ", " : "") + crewReq.Value : "")
                            : when != null ? "by the machine alone" : null;
                string prio = Use("setupBy.priority") is DecisionSite p ? "priority " + p.Value : null;
                d.Text = Join(rule, first, times, crew, prio, keep, Rest());
                return d;
            }
            if (site == Name(LogicSite.Resources))
            {
                d.Verb = Decision.VerbResources;
                string who = group == "processBy" ? "process" : group == "transportBy" ? "transport" : group == "setup" ? "setup crew"
                           : group == "repairBy" ? "repair crew" : group.StartsWith("tasks[", StringComparison.Ordinal) ? "task " + TaskNumber(group) : null;
                string team = (Use("team") ?? Use("processBy.team") ?? Use("setupBy.team"))?.Value;
                var req = Use("requirements") ?? Use("processBy.requirements") ?? Use("setupBy.requirements");
                string prio = (Use("priority") ?? Use("processBy.priority") ?? Use("setupBy.priority")) is DecisionSite p ? "priority " + p.Value : null;
                d.Text = Join((who != null ? who + ": " : "") + (team != null ? "team " + team : "") + (req != null ? (team != null ? ", " : "") + req.Value : ""),
                              prio, Rest()).Trim();
                if (d.Text.EndsWith(":", StringComparison.Ordinal)) d.Text = d.Text.TrimEnd(':');
                return d;
            }
            if (site == Name(LogicSite.Labels))
            {
                d.Verb = Decision.VerbLabels;
                var s = sites[0];
                string when = s.Key == "createLabels" ? "on creation: " : s.Key.EndsWith("entryLabels", StringComparison.Ordinal) ? "on entry: "
                            : s.Key.EndsWith("exitLabels", StringComparison.Ordinal) ? "on exit: " : Short(s.Key, group) + ": ";
                d.Text = when + s.Value.Trim('[', ']');
                return d;
            }
            d.Verb = site == Name(LogicSite.Placement) ? Decision.VerbPlace : site == Name(LogicSite.Replenishment) ? Decision.VerbReorder
                   : site == Name(LogicSite.Dispatch) ? Decision.VerbDispatch
                   : site == Name(LogicSite.Order) ? (group.Length > 0 ? Decision.VerbOrder : Decision.VerbRelease)
                   : site == Name(LogicSite.Routing) ? Decision.VerbRoute : Upper(site);
            if (d.Rule != null) d.Verb = Decision.VerbRule;
            var said = sites.Where(s => !s.IsDefault || d.IsDefault).Select(s => (s.Rule != null ? RuleName(s) : Short(s.Key, group) + " " + s.Value));
            d.Text = (group.Length > 0 && !group.Contains(":") ? GroupLabel(group) + ": " : "") + string.Join(" · ", said);
            return d;
        }

        // one use of a team for the preemption pass: its owner, key group, teams, mode, preemptable and priority text
        private sealed class UseOfTeam
        {
            public LogicOwner Owner; public string Group; public List<string> Teams = new List<string>();
            public string Mode = "None"; public bool Preemptable = true; public string Priority = "1";
        }

        /// <summary>
        /// Who can take a use's members: a use with a preemption mode takes members from the preemptable uses
        /// of its team(s) whose priority is strictly lower. Each such use's resources decision says
        /// <c>preemptable by Rush CNC</c> (<c>(if its priority is higher)</c> when a priority is an expression).
        /// </summary>
        private static void Preemptors(SimulationConfig model, LogicReport report, Func<string, string> nameOf)
        {
            var uses = new List<UseOfTeam>();
            void Collect(LogicOwner o, IEnumerable<(string path, object value)> settings)
            {
                if (o == null) return;
                var values = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var (path, value) in settings) values[path] = value;
                var byGroup = new Dictionary<string, UseOfTeam>(StringComparer.Ordinal);
                foreach (var kv in values)
                {
                    if (!kv.Key.EndsWith(".team", StringComparison.Ordinal) || !(kv.Value is string team) || team.Trim().Length == 0) continue;
                    string group = kv.Key.Substring(0, kv.Key.Length - 5);
                    int req = group.LastIndexOf(".requirements[", StringComparison.Ordinal);
                    if (req > 0 && group.EndsWith("]", StringComparison.Ordinal)) group = group.Substring(0, req);
                    if (!byGroup.TryGetValue(group, out var u))
                    {
                        u = byGroup[group] = new UseOfTeam { Owner = o, Group = group, Preemptable = !group.EndsWith("repairBy", StringComparison.Ordinal) };
                        if (values.TryGetValue(group + ".preemption", out var m) && m != null) u.Mode = m.ToString();
                        if (values.TryGetValue(group + ".preemptable", out var p) && p is bool b) u.Preemptable = b;
                        if (values.TryGetValue(group + ".priority", out var pr) && pr is string ps && ps.Trim().Length > 0) u.Priority = ps.Trim();
                    }
                    string name = nameOf(team.Trim());
                    if (!u.Teams.Contains(name)) u.Teams.Add(name);
                }
                uses.AddRange(byGroup.Values);
            }
            foreach (var e in model.Elements)
                if (e.Settings != null) Collect(report.Owners.FirstOrDefault(x => x.Kind == "element" && x.Id == e.Id), e.Values());
            foreach (var t in model.Downtimes)
                if (t.Settings != null) Collect(report.Owners.FirstOrDefault(x => x.Kind == "downtime" && x.Id == t.Id), t.Values());
            if (!uses.Any(u => u.Mode != "None")) return;
            double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
            foreach (var v in uses)
            {
                if (!v.Preemptable) continue;
                var by = new List<string>();
                foreach (var u in uses)
                {
                    if (u == v || u.Mode == "None" || !u.Teams.Any(v.Teams.Contains)) continue;
                    double? pu = Number(u.Priority), pv = Number(v.Priority);
                    if (pu != null && pv != null && !(pu > pv)) continue;   // equal or lower priority never preempts
                    string who = u.Owner == v.Owner ? u.Owner.Name + " (" + u.Group + ")" : u.Owner.Name;
                    who += pu == null || pv == null ? " (if its priority is higher)" : "";
                    if (!by.Contains(who)) by.Add(who);
                }
                if (by.Count == 0) continue;
                var d = v.Owner.Decisions.FirstOrDefault(x => x.Site == Name(LogicSite.Resources) && x.Sites.Any(s => s.Key.StartsWith(v.Group + ".", StringComparison.Ordinal)));
                if (d == null) continue;
                d.Text = string.Join(" · ", new[] { d.Text, "preemptable by " + string.Join(", ", by) }.Where(x => !string.IsNullOrEmpty(x)));
                d.IsDefault = false;
            }
        }

        private static string TaskNumber(string group)
        {
            int open = group.IndexOf('['), close = group.IndexOf(']');
            return int.TryParse(group.Substring(open + 1, close - open - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
                ? (i + 1).ToString(CultureInfo.InvariantCulture) : group;
        }

        private static string GroupLabel(string group)
        {
            int i = group.IndexOf('[');
            return i < 0 ? group : group.Substring(0, i) + " " + TaskNumber(group);
        }

        // a list query in words: |priority desc -> by priority desc; kind == 1|age -> where kind == 1, by age; a named order
        // name: |q -> name (by q)
        private static string QueryText(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return q;
            int colon = q.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && q.IndexOf('|') > colon) return q.Substring(0, colon) + " (" + QueryText(q.Substring(colon + 2)) + ")";
            int bar = q.IndexOf('|');
            if (bar < 0) return q;
            string where = q.Substring(0, bar).Trim().TrimEnd(';'), by = q.Substring(bar + 1).Trim();
            return string.Join(", ", new[] { where.Length > 0 ? "where " + where : null, by.Length > 0 ? "by " + by : null }.Where(x => x != null));
        }

        // a key without its group: outputStrategy.parameterKey -> parameterKey
        private static string Short(string key, string group)
            => group.Length > 0 && key.StartsWith(group + ".", StringComparison.Ordinal) ? key.Substring(group.Length + 1) : key;

        private static string Upper(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // a rule with its parameters: ShortestQueueRule (FreeWeight = 0.5)
        private static string RuleName(DecisionSite s)
        {
            if (s == null) return "(no rule)";
            string name = s.Rule ?? s.Value;
            var p = s.Parameters.Select(x => x.StartsWith(SimuLean.Rules.ChoiceRules.Key + ".", StringComparison.Ordinal)
                                               ? x.Substring(SimuLean.Rules.ChoiceRules.Key.Length + 1) : x).ToList();
            return p.Count == 0 ? name : $"{name} ({string.Join(", ", p)})";
        }

        // a reaction: When <signal>[ if <condition>]: <action>; <action>
        private static Decision ReactionDecision(LogicOwner o)
        {
            var d = new Decision { Site = Name(LogicSite.Reaction), Verb = Decision.VerbWhen, Sites = new List<DecisionSite>(o.Sites) };
            string signal = o.Sites.FirstOrDefault(s => s.Key == "signal")?.Value ?? o.Type;
            string condition = o.Sites.FirstOrDefault(s => s.Key == "condition")?.Value;
            var actions = o.Sites.Where(s => s.Key.StartsWith("actions[", StringComparison.Ordinal)).ToList();
            d.Text = signal + (condition != null ? " if " + condition : "") + ": "
                   + (actions.Count == 0 ? "nothing" : string.Join("; ", actions.Select(a => a.Value)));
            d.ViewOnly = actions.Count > 0 && actions.All(a => a.ViewOnly);
            foreach (var s in o.Sites)
            {
                foreach (string r in s.Reads) if (!d.Reads.Contains(r)) d.Reads.Add(r);
                foreach (string w in s.Writes) if (!d.Writes.Contains(w)) d.Writes.Add(w);
                foreach (string n in s.Refs) if (n != o.Name && !d.Links.Contains(n)) d.Links.Add(n);
                d.Problem = d.Problem ?? s.Problem;
            }
            return d;
        }
    }
}
