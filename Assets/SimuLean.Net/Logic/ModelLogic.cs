using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Collections;
using SimuLean.Expressions;
using SimuLean.Serialization;
using SimuLean.Signals;

namespace SimuLean.Logic
{
    /// <summary>
    /// One decision site of a model (the logic viewer): what decides it — a setting's value, an expression, a
    /// query or order, a reaction's action — with what it reads and what it writes. Plain data, JSON-friendly
    /// (<see cref="ToData"/>).
    /// </summary>
    public sealed class DecisionSite
    {
        /// <summary>The kind of decision (<see cref="LogicSite"/>, lower-case first: <c>routing</c>, <c>admission</c>, <c>order</c>…).</summary>
        public string Site;
        /// <summary>Where it is: the setting's path (<c>outputStrategy.scoreExpression</c>), or <c>pull:List</c>, <c>signal</c>, <c>actions[0]</c>.</summary>
        public string Key;
        /// <summary>What decides it: the value, the expression's or the query's text, an action as a sentence.</summary>
        public string Value;
        /// <summary>The value is the default (the setting is not given).</summary>
        public bool IsDefault;
        /// <summary>The expression context its text is written in (<c>admission</c>, <c>score</c>…; null = none).</summary>
        public string Context;
        /// <summary>What it reads (<c>item.family</c>, <c>element.open</c>, <c>content('Buffer')</c>…; <see cref="Expression.Reads"/>).</summary>
        public List<string> Reads = new List<string>();
        /// <summary>What it writes (an item label, an element property, a state, a list); a view-only action writes nothing.</summary>
        public List<string> Writes = new List<string>();
        /// <summary>It changes only what is seen (Set Item Look): nothing the model reads.</summary>
        public bool ViewOnly;
        /// <summary>Its text does not parse (the message), else null.</summary>
        public string Problem;
        /// <summary>A choice rule decides it: the rule's type name, else null.</summary>
        public string Rule;
        /// <summary>A rule's parameters as <c>rule.Field = value</c> (their catalog keys under the owner).</summary>
        public List<string> Parameters = new List<string>();
        /// <summary>The rung of the escalation ladder that decides it (<see cref="ModelLogic.Rungs"/>):
        /// <c>field</c> (an option, a number, a name), <c>expression</c>, <c>query</c> (a list query or order), <c>reaction</c>
        /// or <c>rule</c> (a choice rule in C#).</summary>
        public string Rung = ModelLogic.RungField;
        /// <summary>A rule site: every parameter of the rule has its initial value (no record changes it).</summary>
        internal bool RuleAtDefaults = true;
        /// <summary>The model objects it names (an element read by <c>content('B')</c>, a list pushed to, a reaction's target,
        /// a team): the links of the viewer.</summary>
        public List<string> Refs = new List<string>();

        /// <summary>The raw setting: <c>outputStrategy.scoreExpression = -out.content</c>.</summary>
        public string Setting => $"{Key} = {Value}";

        /// <summary>One line: <c>routing: outputStrategy.scoreExpression = -out.content [reads out.content]</c>.</summary>
        public string Line
        {
            get
            {
                string s = $"{Site}: {Key} = {Value}";
                if (IsDefault) s += " (default)";
                if (Reads.Count > 0) s += " [reads " + string.Join(", ", Reads) + "]";
                if (Writes.Count > 0) s += " [writes " + string.Join(", ", Writes) + "]";
                if (ViewOnly) s += " [view only]";
                if (Parameters.Count > 0) s += " [parameters " + string.Join(", ", Parameters) + "]";
                if (Problem != null) s += " [does not parse: " + Problem + "]";
                return s;
            }
        }

        /// <summary>As plain data (absent fields left out).</summary>
        public Dictionary<string, object> ToData()
        {
            var d = new Dictionary<string, object> { ["site"] = Site, ["key"] = Key, ["value"] = Value };
            if (IsDefault) d["default"] = true;
            if (Context != null) d["context"] = Context;
            if (Reads.Count > 0) d["reads"] = Reads;
            if (Writes.Count > 0) d["writes"] = Writes;
            if (ViewOnly) d["viewOnly"] = true;
            if (Rule != null) d["rule"] = Rule;
            if (Parameters.Count > 0) d["parameters"] = Parameters;
            if (Problem != null) d["problem"] = Problem;
            return d;
        }
    }

    /// <summary>An element, team, list, reaction or downtime with its decision sites.</summary>
    public sealed class LogicOwner
    {
        /// <summary><c>element</c>, <c>team</c>, <c>list</c>, <c>reaction</c> or <c>downtime</c>.</summary>
        public string Kind;
        /// <summary>Its name (a reaction's / downtime's id).</summary>
        public string Name;
        /// <summary>Its id in the model (an element's, a team's, a list's; a reaction's / downtime's id).</summary>
        public string Id;
        /// <summary>Its type (an element's engine type, a reaction's signal, a downtime's kind).</summary>
        public string Type;
        /// <summary>An element's next elements in connection order; a reaction's sources; a downtime's targets.</summary>
        public List<string> Next = new List<string>();
        /// <summary>Its decision sites, in the order of its settings.</summary>
        public List<DecisionSite> Sites = new List<DecisionSite>();
        /// <summary>An element: the writes that offer its refused items again (the admission wake-up, <c>SimContext.WatchAdmissions</c>).</summary>
        public List<string> WokenBy = new List<string>();
        /// <summary>A team: the uses that ask it for members; a list: who pushes to it and who pulls from it.</summary>
        public List<string> UsedBy = new List<string>();
        /// <summary>Facts about its logic worth checking (a condition nothing wakes…).</summary>
        public List<string> Notes = new List<string>();
        /// <summary>An element: the reactions whose writes wake its admission (their names).</summary>
        public List<string> WokenByReactions = new List<string>();
        /// <summary>Its decisions in plain language: its sites grouped by what they decide together (a routing's strategy, score and
        /// full-output rule are one decision), in the order of its settings (<see cref="ModelLogic"/>).</summary>
        public List<Decision> Decisions = new List<Decision>();
        /// <summary>It has a decision that is not the default (or a wake-up or a note).</summary>
        public bool HasOwnLogic => Decisions.Any(d => !d.IsDefault) || WokenBy.Count > 0 || Notes.Count > 0;

        /// <summary>As plain data (empty lists left out): its <c>decisions</c> (<see cref="Decision.ToData"/>).</summary>
        public Dictionary<string, object> ToData()
        {
            var d = new Dictionary<string, object> { ["name"] = Name, ["type"] = Type };
            if (Next.Count > 0) d[Kind == "reaction" ? "sources" : Kind == "downtime" ? "targets" : "next"] = Next;
            if (UsedBy.Count > 0) d["usedBy"] = UsedBy;
            d["decisions"] = Decisions.Select(x => x.ToData()).ToList();
            if (WokenBy.Count > 0) d["wokenBy"] = WokenBy;
            if (Notes.Count > 0) d["notes"] = Notes;
            return d;
        }
    }

    /// <summary>One entry of the admission wake-up map: a write of <see cref="Property"/> (any element's; null = any
    /// property) offers <see cref="Element"/>'s refused items again.</summary>
    public sealed class WakeUp
    {
        /// <summary>The element whose admission condition reads the property.</summary>
        public string Element;
        /// <summary>The property name (null = any: the condition computes the name).</summary>
        public string Property;
        /// <summary>The reaction actions that write it (<c>reaction 'Open' actions[0]</c>); empty = nothing in the model does.</summary>
        public List<string> WrittenBy = new List<string>();
    }

    /// <summary>The logic report of a model (<see cref="ModelLogic.Of"/>).</summary>
    public sealed class LogicReport
    {
        /// <summary>The elements, teams, lists, reactions and downtimes, in that order (each in model order).</summary>
        public List<LogicOwner> Owners = new List<LogicOwner>();
        /// <summary>The admission wake-up map.</summary>
        public List<WakeUp> WakeUps = new List<WakeUp>();
        /// <summary>Every owner's notes, prefixed by nothing (they name the owner).</summary>
        public List<string> Notes => Owners.SelectMany(o => o.Notes).ToList();

        /// <summary>The owners of a kind.</summary>
        public IEnumerable<LogicOwner> OfKind(string kind) => Owners.Where(o => o.Kind == kind);

        /// <summary>The owner named <paramref name="name"/> (an element first), or null.</summary>
        public LogicOwner Find(string name, string kind = null)
            => Owners.FirstOrDefault(o => o.Name == name && (kind == null || o.Kind == kind));

        /// <summary>
        /// As plain data: <c>elements</c>, <c>teams</c>, <c>lists</c>, <c>reactions</c>, <c>downtimes</c> (each owner with its
        /// <c>decisions</c>; an element without any — no decision, wake-up or note — is left out; every downtime is listed, with its <c>targets</c>), <c>wakeUps</c> and
        /// <c>notes</c>. <paramref name="only"/> = the owners of that name.
        /// </summary>
        public Dictionary<string, object> ToData(string only = null)
        {
            bool Wanted(LogicOwner o) => only == null ? o.Kind == "downtime" || o.Decisions.Count > 0 || o.WokenBy.Count > 0 || o.Notes.Count > 0 : o.Name == only;
            var d = new Dictionary<string, object>();
            foreach (var (kind, key) in new[] { ("element", "elements"), ("team", "teams"), ("list", "lists"), ("reaction", "reactions"), ("downtime", "downtimes") })
            {
                var list = OfKind(kind).Where(Wanted).Select(o => (object)o.ToData()).ToList();
                if (list.Count > 0) d[key] = list;
            }
            if (only != null && d.Count == 0)
                throw new ArgumentException($"The model has nothing named '{only}'." + NameSuggestions.DidYouMean(only, Owners.Select(o => o.Name)));
            var wakes = WakeUps.Where(w => only == null || w.Element == only).Select(w => (object)new Dictionary<string, object>
            {
                ["element"] = w.Element, ["property"] = w.Property ?? "(any)", ["writtenBy"] = w.WrittenBy,
            }).ToList();
            if (wakes.Count > 0) d["wakeUps"] = wakes;
            var notes = Owners.Where(o => only == null || o.Name == only).SelectMany(o => o.Notes).ToList();
            if (notes.Count > 0) d["notes"] = notes;
            return d;
        }

        /// <summary>The decisions worth showing beside an element that holds items in a diagnosis (the marks of
        /// <c>simulean_diagnose</c>): its own routing, order, pull and wait decisions that are not the default, the admission of
        /// each next element, and the notes of both, as sentences (with their run-time counters when <see cref="ModelLogic.Live"/>
        /// attached them). At most <paramref name="max"/> lines.</summary>
        public List<string> Marks(string element, int max = 8)
        {
            var marks = new List<string>();
            var owner = Find(element, "element");
            if (owner == null) return marks;
            string Mark(Decision d) => d.Sentence + (d.LiveText != null ? " [" + d.LiveText + "]" : "");
            foreach (var d in owner.Decisions)
                if (!d.IsDefault && (d.Site == "routing" || d.Site == "order" || d.Site == "wait" || d.Verb == Decision.VerbPull))
                    marks.Add(Mark(d));
            foreach (string n in owner.Notes) marks.Add(n);
            foreach (string name in owner.Next)
            {
                var next = Find(name, "element");
                if (next == null) continue;
                foreach (var d in next.Decisions)
                    if (d.Site == "admission" && !d.IsDefault && d.Verb != Decision.VerbPull) marks.Add($"next {name}: {Mark(d)}");
                foreach (string n in next.Notes) marks.Add($"next {name}: {n}");
            }
            return marks.Distinct().Take(max).ToList();
        }

        // ── scripted logic (the card's scripts, simulean_rule_show, simulean_validate) ──

        /// <summary>Every decision site a choice rule decides, with its owner; <paramref name="rule"/> = only that rule's (its
        /// type name or full name).</summary>
        public List<(LogicOwner Owner, DecisionSite Site)> RuleSites(string rule = null)
        {
            var t = rule == null ? null : SimuLean.Rules.ChoiceRules.Find(rule);
            var list = new List<(LogicOwner, DecisionSite)>();
            foreach (var o in Owners)
                foreach (var s in o.Sites)
                    if (s.Rule != null && (rule == null || s.Rule == rule.Trim() || (t != null && SimuLean.Rules.ChoiceRules.Find(s.Rule) == t)))
                        list.Add((o, s));
            return list;
        }

        /// <summary>The catalog key of a rule parameter of an owner (<c>Router.rule.Weight</c>, a team's <c>team:Ops.rule.Weight</c>)
        /// from its <see cref="DecisionSite.Parameters"/> entry (<c>rule.Weight = 0</c>).</summary>
        public static string CatalogKey(LogicOwner owner, string parameter)
            => (owner.Kind == "team" ? "team:" : "") + owner.Name + "." + parameter.Split(' ')[0];

        /// <summary>One use of a rule as plain data: <c>owner</c>, <c>kind</c>, <c>key</c> (the setting), <c>site</c>, the
        /// <c>parameters</c> with their values and their <c>catalog</c> keys (<c>simulean_rule_show</c>'s <c>usedBy</c>).</summary>
        public static Dictionary<string, object> RuleUse(LogicOwner owner, DecisionSite site)
            => new Dictionary<string, object>
            {
                ["owner"] = owner.Name, ["kind"] = owner.Kind, ["key"] = site.Key, ["site"] = site.Site, ["parameters"] = site.Parameters,
                ["catalog"] = site.Parameters.Select(p => CatalogKey(owner, p)).ToList(),
            };

        /// <summary>
        /// The decisions of the model by the rung that makes them: one decision per owner and site kind (an element's routing,
        /// its admission, a team's dispatch, a reaction…) that is not the default, counted at the highest rung among its
        /// settings (a BestScore routing is an expression, a routing by a rule a rule). Every rung of <see cref="ModelLogic.Rungs"/>
        /// is present (0 when none).
        /// </summary>
        public Dictionary<string, int> Decisions()
        {
            var counts = ModelLogic.Rungs.ToDictionary(r => r, r => 0);
            foreach (var o in Owners)
                foreach (var g in o.Sites.Where(s => !s.IsDefault).GroupBy(s => s.Site))
                    counts[g.Select(s => s.Rung).OrderByDescending(r => Array.IndexOf(ModelLogic.Rungs, r)).First()]++;
            return counts;
        }

        /// <summary>
        /// The model's scripted logic (the model card's <c>scripts</c>): <c>rules</c> — each use of a choice rule
        /// with its owner, setting, site, the rule's label, <c>why</c>, declared reads, parameters (values and catalog keys) and
        /// source hash —, <c>decisions</c> — the non-default decisions by rung (<see cref="Decisions"/>) —, <c>declarative</c> /
        /// <c>scripted</c> — how many of them each — and a one-line <c>summary</c>.
        /// </summary>
        public Dictionary<string, object> Scripts()
        {
            var rules = RuleSites().Select(x =>
            {
                var d = RuleUse(x.Owner, x.Site);
                var t = SimuLean.Rules.ChoiceRules.Find(x.Site.Rule);
                var a = SimuLean.Rules.ChoiceRules.Describe(t);
                d["rule"] = t?.Name ?? x.Site.Rule;
                d["label"] = a?.Label ?? t?.Name ?? x.Site.Rule;
                d["why"] = string.IsNullOrWhiteSpace(a?.Why) ? null : a.Why;
                if (x.Site.Reads.Count > 0) d["reads"] = x.Site.Reads;
                string hash = t == null ? null : SimuLean.Rules.ChoiceRules.SourceHash(t);
                if (hash != null) d["sourceHash"] = hash;
                if (x.Site.Problem != null) d["problem"] = x.Site.Problem;
                return (object)d;
            }).ToList();
            var decisions = Decisions();
            int scripted = decisions[ModelLogic.RungRule], declarative = decisions.Values.Sum() - scripted;
            string parts = string.Join(", ", ModelLogic.Rungs.Where(r => r != ModelLogic.RungRule && decisions[r] > 0).Select(r => decisions[r] + " " + r));
            string summary = declarative + scripted == 0
                ? "every decision is the elements' default"
                : $"{declarative} of {declarative + scripted} decisions declarative" + (parts.Length > 0 ? $" ({parts})" : "") +
                  $", {scripted} by a choice rule";
            return new Dictionary<string, object>
            {
                ["rules"] = rules, ["decisions"] = decisions, ["declarative"] = declarative, ["scripted"] = scripted, ["summary"] = summary,
            };
        }

        /// <summary>
        /// The warnings about the model's choice rules (<c>simulean_validate</c>, <c>Study.Check</c>): a rule without a
        /// <c>Why</c> (it must name the declarative form tried first), and a rule whose <see cref="SimuLean.Rules.ChoiceRuleAttribute.Equivalent"/>
        /// declares the primitive it decides exactly as, used with every parameter at its initial value — the primitive is
        /// the lower rung. <paramref name="varied"/> (a catalog key → a study varies it) keeps the second quiet when a study varies
        /// one of the rule's parameters.
        /// </summary>
        public List<string> RuleWarnings(Func<string, bool> varied = null)
        {
            var warnings = new List<string>();
            foreach (var (o, s) in RuleSites())
            {
                var t = SimuLean.Rules.ChoiceRules.Find(s.Rule);
                if (t == null) continue;   // the build reports a missing rule
                var a = SimuLean.Rules.ChoiceRules.Describe(t);
                if (string.IsNullOrWhiteSpace(a?.Why))
                    warnings.Add($"Rule '{t.Name}' (used by {o.Name}, {s.Key}) has no Why: give it [ChoiceRule(Why = \"...\")] naming the " +
                                 "declarative form tried first (a field, an expression, a query / order, a reaction) and why it is not enough.");
                if (!string.IsNullOrWhiteSpace(a?.Equivalent) && s.RuleAtDefaults
                    && !(varied != null && s.Parameters.Any(p => varied(CatalogKey(o, p)))))
                    warnings.Add($"{o.Name}: {s.Key} = {t.Name} with its parameters at their initial values decides exactly as " +
                                 $"{a.Equivalent.Trim()}: use that declarative form (a choice rule is the last rung of the ladder).");
            }
            return warnings.Distinct().ToList();
        }
    }

    /// <summary>
    /// The logic viewer's report: every decision site of a model — where items go, in which
    /// order, what an element takes, who serves a job and in which order a team serves, list queries and orders, reactions —
    /// with what decides each and what it reads / writes, and the admission wake-up map. Built from the canonical model (the
    /// <see cref="SimulationConfig"/> a run builds), so a scene and a headless run show the same; read-only, it builds nothing
    /// and schedules nothing. What a setting decides is declared on the setting itself (<see cref="SettingAttribute.Logic"/>,
    /// <see cref="SettingAttribute.Form"/>, <see cref="SettingAttribute.Context"/>), so a new setting is described where it
    /// is declared; <see cref="Undeclared"/> lists the settings that look like decisions and declare nothing (a test fails on
    /// them). Read by <c>simulean_model section=logic</c>, the editor window <i>Tools ▸ SimuLean ▸ Model Logic</i> and the
    /// marks of <c>simulean_diagnose</c>.
    /// </summary>
    public static partial class ModelLogic
    {
        /// <summary>The rungs of the escalation ladder (<c>docs/agents/topics/logic.md</c> <i>The escalation
        /// ladder</i>), lowest first: a field, an expression, a query / order, a reaction, a choice rule.</summary>
        public const string RungField = "field", RungExpression = "expression", RungQuery = "query", RungReaction = "reaction", RungRule = "rule";

        /// <summary>The rungs in order, lowest first.</summary>
        public static readonly string[] Rungs = { RungField, RungExpression, RungQuery, RungReaction, RungRule };

        /// <summary>The report of <paramref name="model"/>.</summary>
        public static LogicReport Of(SimulationConfig model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var report = new LogicReport();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in model.Elements) if (e.Id != null) names[e.Id] = e.Name ?? e.Id;
            foreach (var l in model.Lists) if (l.Id != null) names[l.Id] = l.Name ?? l.Id;
            foreach (var t in model.ResourceTeams) if (t.Id != null) names[t.Id] = t.Name ?? t.Id;
            string NameOf(string id) => id != null && names.TryGetValue(id, out var n) ? n : id;

            var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var c in model.Connections)
                if (ConnectionConfig.EffectiveType(c) == "General")
                {
                    if (!next.TryGetValue(c.SourceId ?? "", out var list)) next[c.SourceId ?? ""] = list = new List<string>();
                    list.Add(NameOf(c.TargetId));
                }

            // elements
            var teamUses = TeamUses(model, NameOf);
            foreach (var e in model.Elements)
            {
                var o = new LogicOwner { Kind = "element", Name = e.Name ?? e.Id, Id = e.Id, Type = e.Type };
                if (next.TryGetValue(e.Id ?? "", out var n)) o.Next.AddRange(n);
                if (e.Settings != null) Walk(e.Settings, e.Settings.GetType(), "", o.Sites);
                foreach (var s in o.Sites) if (s.Rule != null) RuleParameters(s, e.Settings?.Rule);
                // the element's own routing matters only with two outputs or more
                if (o.Next.Count < 2) o.Sites.RemoveAll(s => s.Site == "routing" && s.IsDefault);
                foreach (var s in o.Sites)
                    if (s.Site == "resources" && (s.Key == "team" || s.Key.EndsWith(".team", StringComparison.Ordinal)) && !s.IsDefault)
                    {
                        string team = NameOf(s.Value);
                        s.Value = team;
                        s.Refs.Add(team);
                    }
                foreach (var c in model.Connections)
                {
                    if (c.ConnectionType == "PullFromList" && c.TargetId == e.Id)
                    {
                        bool none = string.IsNullOrWhiteSpace((c.Query ?? "").Replace("|", "").Replace(";", ""));
                        var s = new DecisionSite
                        {
                            Site = Name(LogicSite.Admission), Key = "pull:" + NameOf(c.SourceId),
                            Value = none ? "the list's order" : c.Query, IsDefault = none,
                            Rung = RungQuery, Refs = { NameOf(c.SourceId) },
                        };
                        if (!s.IsDefault) QueryReads(c.Query, s);
                        o.Sites.Add(s);
                    }
                    else if (c.ConnectionType == "PushToList" && c.SourceId == e.Id)
                        o.Sites.Add(new DecisionSite { Site = Name(LogicSite.Routing), Key = "push:" + NameOf(c.TargetId), Value = "every item", Writes = { "list " + NameOf(c.TargetId) }, Refs = { NameOf(c.TargetId) } });
                }
                report.Owners.Add(o);
            }

            // teams
            foreach (var t in model.ResourceTeams)
            {
                var o = new LogicOwner { Kind = "team", Name = t.Name ?? t.Id, Id = t.Id, Type = "ResourceTeam" };
                Walk(t, typeof(ResourceTeamConfig), "", o.Sites);
                foreach (var s in o.Sites) if (s.Rule != null) RuleParameters(s, t.Rule);
                if (teamUses.TryGetValue(o.Name, out var uses)) o.UsedBy.AddRange(uses);
                report.Owners.Add(o);
            }
            RequestPriorities(model, report, NameOf);

            // lists
            foreach (var l in model.Lists)
            {
                var o = new LogicOwner { Kind = "list", Name = l.Name ?? l.Id, Id = l.Id, Type = "List" };
                bool ordered = !string.IsNullOrWhiteSpace(l.DefaultOrderBy);
                var order = new DecisionSite
                {
                    Site = Name(LogicSite.Order), Key = "order", IsDefault = !ordered,
                    Value = ordered ? l.DefaultOrderBy + (l.DefaultDescending ? " desc" : "") : "push order (first in, first out)",
                    Rung = RungQuery,
                };
                if (ordered) QueryReads("|" + order.Value, order);
                o.Sites.Add(order);
                foreach (var f in l.Fields.Where(f => f != null && f.kind == ListFieldKind.Expression && !string.IsNullOrWhiteSpace(f.source)))
                {
                    var s = new DecisionSite { Site = Name(LogicSite.Order), Key = "fields." + f.name, Value = f.source, Context = ExpressionContext.ListField.Key, Rung = RungExpression };
                    ExpressionReads(f.source, ExpressionContext.ListField, s);
                    o.Sites.Add(s);
                }
                foreach (var c in model.Connections)
                {
                    if (c.ConnectionType == "PushToList" && c.TargetId == l.Id) o.UsedBy.Add("pushed by " + NameOf(c.SourceId));
                    if (c.ConnectionType == "PullFromList" && c.SourceId == l.Id)
                        o.UsedBy.Add("pulled by " + NameOf(c.TargetId) + (string.IsNullOrWhiteSpace(c.Query) ? "" : " where " + c.Query));
                }
                report.Owners.Add(o);
            }

            // reactions
            var propertyWriters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var retries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var propertyReactions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var r in model.Reactions)
            {
                var o = Reaction(r, NameOf);
                report.Owners.Add(o);
                var rs = r.Settings ?? new ReactionSettings();
                int k = 0;
                foreach (var a in rs.Actions ?? new List<ReactionActionSettings>())
                {
                    k++;
                    if (a == null || !rs.ReactionEnabled) continue;
                    string by = $"reaction '{r.Id}' actions[{k - 1}]";
                    if (a.Kind == ReactionActionKind.SetElementProperty && !string.IsNullOrWhiteSpace(a.Name))
                    {
                        Add(propertyWriters, a.Name.Trim(), by);
                        Add(propertyReactions, a.Name.Trim(), r.Id);
                    }
                    if (a.Kind == ReactionActionKind.Retry)
                        foreach (string target in string.IsNullOrWhiteSpace(a.Target) ? r.Sources.Select(NameOf) : new[] { NameOf(a.Target.Trim()) })
                            Add(retries, target, by);
                }
            }

            // downtimes (their repair crews); one without a decision too: what it stops is logic a reader looks for
            foreach (var t in model.Downtimes)
            {
                var o = new LogicOwner { Kind = "downtime", Name = t.Id, Id = t.Id, Type = t.Kind };
                o.Next.AddRange(t.TargetIds.Select(NameOf));
                if (t.Settings != null) Walk(t.Settings, typeof(DowntimeSettings), "", o.Sites);
                report.Owners.Add(o);
            }

            WakeUps(model, report, propertyWriters, retries, propertyReactions);
            // ids written in values (a requirement's team, a timeout target, a release point, a repair crew) as names
            var ids = names.Where(kv => kv.Key != kv.Value && kv.Key.Length > 0).OrderByDescending(kv => kv.Key.Length).ToList();
            foreach (var o in report.Owners)
                foreach (var s in o.Sites)
                {
                    if (s.Value == null || o.Kind == "reaction") continue;
                    foreach (var kv in ids)
                        if (s.Value.Contains(kv.Key))
                        {
                            s.Value = s.Value.Replace(kv.Key, kv.Value);
                            if (!s.Refs.Contains(kv.Value)) s.Refs.Add(kv.Value);
                        }
                    if ((s.Key.EndsWith("timeoutTarget", StringComparison.Ordinal) || s.Key.EndsWith("releaseAt", StringComparison.Ordinal)
                         || s.Key.EndsWith("team", StringComparison.Ordinal)) && !s.IsDefault && !s.Refs.Contains(s.Value.Trim()))
                        s.Refs.Add(s.Value.Trim());
                }
            foreach (var o in report.Owners) Decide(o);
            Preemptors(model, report, NameOf);
            return report;
        }

        /// <summary>
        /// Every use of each team in <paramref name="model"/> (including a requirement's team and a repair crew), by team name: each element setting <c>….team</c> — the process team and its
        /// requirements' teams (<c>processBy</c>), the transport (<c>transportBy</c>), the setup crew (<c>setup.setupBy</c>),
        /// an Assembly Station's tasks (<c>tasks[i]</c>) — and each downtime's repair crew (<c>repairBy</c>), one entry per
        /// owner and use: <c>CraneLift (processBy)</c>, <c>downtime Breakdowns (repairBy)</c>. Read from the config, so a scene
        /// and a headless run list the same (the scene's connection lines, <c>SceneConnections</c>, walk the same uses on the
        /// Unity components).
        /// </summary>
        public static Dictionary<string, List<string>> TeamUses(SimulationConfig model, Func<string, string> nameOf = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            nameOf = nameOf ?? (x => x);
            var uses = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Collect(string owner, IEnumerable<(string path, object value)> settings)
            {
                foreach (var (path, value) in settings)
                {
                    if (!(value is string team) || team.Trim().Length == 0) continue;
                    string group;
                    if (path == "team") group = "";
                    else if (path.EndsWith(".team", StringComparison.Ordinal)) group = path.Substring(0, path.Length - 5);
                    else continue;
                    // a requirement's team is a team of its use: processBy.requirements[1] → processBy
                    int req = group.LastIndexOf("requirements[", StringComparison.Ordinal);
                    if (req >= 0 && group.EndsWith("]", StringComparison.Ordinal)) group = group.Substring(0, req).TrimEnd('.');
                    Add(uses, nameOf(team.Trim()), group.Length > 0 ? $"{owner} ({group})" : owner);
                }
            }
            foreach (var e in model.Elements)
                if (e.Settings != null) Collect(e.Name ?? e.Id, e.Values());
            foreach (var t in model.Downtimes)
                if (t.Settings != null) Collect("downtime " + t.Id, t.Values());
            return uses;
        }

        /// <summary>
        /// A requester's priority (<c>processBy.priority</c>, <c>transportBy.priority</c>, a task's or a repair crew's) is a
        /// decision when its team's queue can tell requests apart by it: the team orders its requests by priority (its request
        /// order, or the named order it chooses, reads <c>priority</c>), or its requesters ask with different priorities
        /// (preemption compares them). Then every requester of the team shows its priority, the default 1 included.
        /// </summary>
        private static void RequestPriorities(SimulationConfig model, LogicReport report, Func<string, string> nameOf)
        {
            var byTeam = new Dictionary<string, List<(LogicOwner owner, string group, string priority)>>(StringComparer.Ordinal);
            foreach (var e in model.Elements)
            {
                var o = report.Owners.FirstOrDefault(x => x.Kind == "element" && x.Id == e.Id);
                if (o == null || e.Settings == null) continue;
                var values = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var (path, value) in e.Values()) values[path] = value;
                foreach (var kv in values)
                {
                    if (!kv.Key.EndsWith(".team", StringComparison.Ordinal)) continue;
                    string team = kv.Value as string;
                    if (string.IsNullOrWhiteSpace(team)) continue;
                    string group = kv.Key.Substring(0, kv.Key.Length - 5);
                    // a requirement's own team (processBy.requirements[1].team) asks with its use's priority: a requirement
                    // has no priority of its own
                    int req = group.LastIndexOf(".requirements[", StringComparison.Ordinal);
                    if (req > 0 && group.EndsWith("]", StringComparison.Ordinal)) group = group.Substring(0, req);
                    string prio = values.TryGetValue(group + ".priority", out var p) && p is string ps && ps.Trim().Length > 0 ? ps.Trim() : "1";
                    team = nameOf(team.Trim());
                    if (!byTeam.TryGetValue(team, out var list)) byTeam[team] = list = new List<(LogicOwner, string, string)>();
                    list.Add((o, group, prio));
                }
            }
            foreach (var t in model.ResourceTeams)
            {
                string name = t.Name ?? t.Id;
                if (name == null || !byTeam.TryGetValue(name, out var requesters)) continue;
                string order = (t.RequestOrder ?? "").Trim();
                var named = t.NamedOrders?.FirstOrDefault(n => n != null && n.Name == order);
                if (named != null) order = named.Query ?? "";
                bool ordersByPriority = order.IndexOf("priority", StringComparison.OrdinalIgnoreCase) >= 0;
                bool differ = requesters.Select(r => r.priority).Distinct().Count() > 1;
                if (!ordersByPriority && !differ) continue;
                foreach (var (owner, group, prio) in requesters)
                {
                    string key = group + ".priority";
                    var site = owner.Sites.FirstOrDefault(s => s.Key == key);
                    if (site != null) { site.IsDefault = false; continue; }
                    var teamSite = owner.Sites.FirstOrDefault(s => s.Key == group + ".team");
                    owner.Sites.Add(new DecisionSite
                    {
                        Site = teamSite?.Site ?? Name(LogicSite.Resources), Key = key, Value = prio, Context = "priority",
                    });
                }
            }
        }

        /// <summary>The marks of a diagnosis (<c>ModelDiagnosis.Of</c>, <c>simulean_diagnose</c>): each element that holds items
        /// gets <c>decisions</c> — the decisions involved in where its items wait (<see cref="LogicReport.Marks"/>), with their
        /// run-time counters when <paramref name="run"/> is the model that ran (<see cref="Live"/>).</summary>
        public static void Mark(Dictionary<string, object> diagnosis, SimulationConfig model, SimuLean.Headless.HeadlessModelFactory run = null)
        {
            if (diagnosis == null || !diagnosis.TryGetValue("elements", out var list) || !(list is IEnumerable elements)) return;
            var report = Of(model);
            if (run != null) Live(report, run);
            foreach (var x in elements.OfType<Dictionary<string, object>>())
            {
                if (!(x.TryGetValue("items", out var held) && held is int n && n > 0)) continue;
                var marks = report.Marks(x["name"] as string);
                if (marks.Count > 0) x["decisions"] = marks;
            }
        }

        private static void Add(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
            if (!list.Contains(value)) list.Add(value);
        }

        // the admission wake-up map (SimContext.WatchAdmissions watches what each Condition admission's
        // Expression.ElementPropertiesRead names) and the notes of a condition no write wakes
        private static void WakeUps(SimulationConfig model, LogicReport report, Dictionary<string, List<string>> writers,
                                    Dictionary<string, List<string>> retries, Dictionary<string, List<string>> reactionsOf)
        {
            foreach (var e in model.Elements)
            {
                var input = e.Settings?.InputStrategy;
                if (input == null || BuilderChoices.Admission(input.StrategyType) != HeadlessInputStrategyType.Condition
                    || string.IsNullOrWhiteSpace(input.Condition)) continue;
                var owner = report.Find(e.Name ?? e.Id, "element");
                Expression condition;
                try { condition = Expression.Parse(input.Condition, ExpressionContext.Admission); }
                catch (FormatException) { continue; }   // its site carries the problem
                var names = condition.ElementPropertiesRead();
                if (names == null)
                {
                    report.WakeUps.Add(new WakeUp { Element = owner.Name, Property = null });
                    owner.WokenBy.Add("any property write (the condition computes a property name)");
                    foreach (var writing in reactionsOf.Values)
                        foreach (string r in writing) if (!owner.WokenByReactions.Contains(r)) owner.WokenByReactions.Add(r);
                }
                else
                    foreach (string p in names.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var w = new WakeUp { Element = owner.Name, Property = p };
                        if (writers.TryGetValue(p, out var by)) w.WrittenBy.AddRange(by);
                        report.WakeUps.Add(w);
                        if (reactionsOf.TryGetValue(p, out var writing))
                            foreach (string r in writing) if (!owner.WokenByReactions.Contains(r)) owner.WokenByReactions.Add(r);
                        if (w.WrittenBy.Count > 0) owner.WokenBy.Add($"property '{p}' ({string.Join(", ", w.WrittenBy)})");
                        else if (!((IList<string>)EntityScope.ElementBuiltIns).Contains(p))
                            owner.Notes.Add($"{owner.Name}: its admission condition reads property '{p}', which no reaction writes: no write " +
                                            "wakes it (its refused items are offered again only when something else moves).");
                    }
                // what else it reads changes without a write: a Retry action (or a freed place upstream) offers the items again
                var other = condition.Reads().Where(x => x.Contains("(") || x == "random").ToList();
                if (other.Count > 0)
                {
                    string retried = retries.TryGetValue(owner.Name, out var rs) ? "; retried by " + string.Join(", ", rs)
                                   : $": it is checked again when '{owner.Name}' frees a place; if what it reads changes elsewhere, add a Retry action targeting it";
                    owner.Notes.Add($"{owner.Name}: its admission condition reads {string.Join(", ", other)}, which no property write " +
                                    $"wakes{retried}.");
                }
            }
        }

        // ── reactions ────────────────────────────────────────────────────

        private static LogicOwner Reaction(ReactionConfig r, Func<string, string> nameOf)
        {
            var rs = r.Settings ?? new ReactionSettings();
            var o = new LogicOwner { Kind = "reaction", Name = r.Id, Id = r.Id, Type = rs.Signal.ToString() };
            o.Next.AddRange(r.Sources.Select(nameOf));
            var context = SignalKinds.HasItem(rs.Signal) ? ExpressionContext.ReactionItem
                        : SignalKinds.IsTime(rs.Signal) ? ExpressionContext.ReactionTime : ExpressionContext.ReactionElement;
            string R = Name(LogicSite.Reaction);
            string signal = rs.Signal == SignalKind.Every ? $"every {Num(rs.Period)}" + (rs.FirstTime != null ? $" from {Num(rs.FirstTime)}" : "")
                          : rs.Signal == SignalKind.AtTime ? $"at {Num(rs.AtTime)}" : rs.Signal.ToString();
            if (!string.IsNullOrWhiteSpace(rs.State)) signal += " (state " + rs.State.Trim() + ")";
            if (o.Next.Count > 0) signal += " of " + string.Join(", ", o.Next);
            if (rs.Timing != SignalDelivery.NextEvent) signal += ", " + rs.Timing;
            if (!rs.ReactionEnabled) signal += " (disabled)";
            var sig = new DecisionSite { Site = R, Key = "signal", Value = signal, Rung = RungReaction };
            sig.Refs.AddRange(o.Next);
            o.Sites.Add(sig);
            if (!string.IsNullOrWhiteSpace(rs.Condition))
            {
                var s = new DecisionSite { Site = R, Key = "condition", Value = rs.Condition.Trim(), Context = context.Key, Rung = RungReaction };
                ExpressionReads(s.Value, context, s);
                o.Sites.Add(s);
            }
            int k = 0;
            foreach (var a in rs.Actions ?? new List<ReactionActionSettings>())
            {
                if (a == null) { k++; continue; }
                var s = new DecisionSite { Site = R, Key = $"actions[{k++}]", Context = context.Key, Rung = RungReaction };
                string value = a.Value?.Trim();
                bool number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                if (!string.IsNullOrEmpty(value) && !number) ExpressionReads(value, context, s);
                string target = string.IsNullOrWhiteSpace(a.Target) ? null : nameOf(a.Target.Trim());
                string where = target ?? (o.Next.Count > 0 ? string.Join(" / ", o.Next) : "the signal's element");
                string team = string.IsNullOrWhiteSpace(a.Team) ? null : nameOf(a.Team.Trim());
                string members = team == null ? null : $"team {team} ({(a.Count > 0 ? a.Count.Value.ToString(CultureInfo.InvariantCulture) : "all")} members)";
                if (team != null) s.Refs.Add(team);
                if (target != null) s.Refs.Add(target);
                else if (a.Kind != ReactionActionKind.SetItemLabel && a.Kind != ReactionActionKind.SetItemLook) s.Refs.AddRange(o.Next);
                if (a.Kind == ReactionActionKind.PushToList && !string.IsNullOrWhiteSpace(a.Name)) s.Refs.Add(a.Name.Trim());
                switch (a.Kind)
                {
                    case ReactionActionKind.SetItemLabel:
                        s.Value = $"set item.{a.Name} = {value}"; s.Writes.Add("item." + a.Name); break;
                    case ReactionActionKind.SetElementProperty:
                        s.Value = $"set {where}.{a.Name} = {value}"; s.Writes.Add($"{where}.{a.Name} (property)"); break;
                    case ReactionActionKind.Stop:
                        s.Value = $"stop {members ?? where}" + (string.IsNullOrWhiteSpace(a.State) ? "" : " as " + a.State.Trim()) + $" ({a.Mode})"
                                  + (a.Duration > 0 ? $" for {Num(a.Duration)}" : " until resumed") + (string.IsNullOrWhiteSpace(a.Group) ? "" : $" [group {a.Group}]");
                        s.Writes.Add("state of " + (members ?? where)); break;
                    case ReactionActionKind.Resume:
                        s.Value = $"resume {members ?? where}" + (string.IsNullOrWhiteSpace(a.Group) ? "" : $" [group {a.Group}]");
                        s.Writes.Add("state of " + (members ?? where)); break;
                    case ReactionActionKind.PushToList:
                        s.Value = $"push {value} to list {a.Name}"; s.Writes.Add("list " + a.Name); break;
                    case ReactionActionKind.Release:
                        s.Value = $"release {(string.IsNullOrEmpty(value) ? "1" : value)} item(s) of {where}"; s.Writes.Add($"items held by {where} (released)"); break;
                    case ReactionActionKind.Retry:
                        s.Value = $"retry {where}: offer its waiting items again"; s.Writes.Add($"{where} (its admission is evaluated again)"); break;
                    case ReactionActionKind.SetItemLook:
                        s.Value = "item look" + (string.IsNullOrWhiteSpace(a.Color) ? "" : " " + a.Color.Trim()) + (a.Scale != null ? $" ×{Num(a.Scale)}" : "");
                        s.ViewOnly = true; break;
                    default:
                        s.Value = a.Kind.ToString(); break;
                }
                if (!string.IsNullOrWhiteSpace(a.OnlyIf))
                {
                    s.Value += " only if " + a.OnlyIf.Trim();
                    ExpressionReads(a.OnlyIf.Trim(), context, s);
                }
                o.Sites.Add(s);
            }
            return o;
        }

        private static string Num(double? x) => x == null ? "?" : x.Value.ToString("R", CultureInfo.InvariantCulture);

        // ── settings declared as decisions ───────────────────────────────

        /// <summary>A <see cref="LogicSite"/> as its site name (<c>routing</c>, <c>admission</c>…).</summary>
        public static string Name(LogicSite site) => SettingsPaths.KeyOf(site.ToString());

        private static SettingAttribute Attr(SettingsPaths.Member m)
            => (SettingAttribute)Attribute.GetCustomAttribute(m.Field, typeof(SettingAttribute));

        // every declared decision of a group (and of its groups and records), with its default when the declaration shows it
        private static void Walk(object group, Type type, string prefix, List<DecisionSite> into)
        {
            object fresh = null;
            foreach (var m in SettingsPaths.Members(type))
            {
                object v = group == null ? null : m.Get(group);
                var a = Attr(m);
                string path = prefix + m.Key;
                if (a != null && a.Logic > LogicSite.None)
                {
                    if (fresh == null && !type.IsAbstract) fresh = Activator.CreateInstance(type);
                    var s = Site(group, type, m, a, v, fresh == null ? null : m.Get(fresh), path);
                    if (s != null) into.Add(s);
                    continue;
                }
                if (m.IsGroup) Walk(v, m.Type, path + ".", into);
                else if (m.RecordType != null && v is IList list)
                    for (int i = 0; i < list.Count; i++)
                        Walk(list[i], m.RecordType, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "].", into);
            }
        }

        /// <summary>A <see cref="SettingAttribute.LogicWhen"/> holds for <paramref name="group"/>: every condition
        /// <c>key=v1|v2</c> (the sibling has one of the values; empty = not given) or <c>key!=v1|v2</c> (none of them).</summary>
        internal static bool Applies(string when, Type type, object group)
        {
            foreach (string part in when.Split(';'))
            {
                if (part.Trim().Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0) throw new FormatException($"LogicWhen '{when}' of {type.Name}: a condition is key=value or key!=value.");
                bool not = part[eq - 1] == '!';
                string key = part.Substring(0, not ? eq - 1 : eq).Trim();
                var sibling = SettingsPaths.Find(type, key) ?? throw new FormatException($"LogicWhen '{when}' of {type.Name}: no setting '{key}'.");
                string sv = group == null ? "" : BuilderChoices.Built(sibling, Text(sibling.Get(group))).Trim();
                bool any = part.Substring(eq + 1).Split('|').Any(x => SameText(x, sv));
                if (any == not) return false;
            }
            return true;
        }

        // the same value: as numbers when both are numbers, else as text ignoring case
        private static bool SameText(string a, string b)
        {
            a = (a ?? "").Trim(); b = (b ?? "").Trim();
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return x == y;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // a value written that means the default (LogicDefaultValue, or LogicDefault when it is a value)
        private static bool IsDefaultValue(SettingAttribute a, object v)
        {
            if (v is IList && !(v is string)) return false;
            string text = Text(v);
            if (a.LogicDefault != null && SameText(a.LogicDefault, text)) return true;
            return a.LogicDefaultValue != null && a.LogicDefaultValue.Split('|').Any(x => SameText(x, text));
        }

        private static bool Empty(object v, object fresh)
            => v == null || (v is string s && s.Trim().Length == 0) || (v is IList l && !(v is string) && l.Count == 0)
               || SettingsPaths.SameValue(v, fresh);

        private static DecisionSite Site(object group, Type type, SettingsPaths.Member m, SettingAttribute a, object v, object fresh, string path)
        {
            if (a.LogicWhen != null && !Applies(a.LogicWhen, type, group)) return null;
            var s = new DecisionSite { Site = Name(a.Logic), Key = path };
            if (Empty(v, fresh) || IsDefaultValue(a, v is string text ? BuilderChoices.Built(m, text) : v))
            {
                if (a.LogicDefault == null) return null;
                s.Value = a.LogicDefault;
                s.IsDefault = true;
                return s;
            }
            s.Value = Text(v);
            var form = a.Form == LogicForm.Value && a.Role == SettingRole.Expression ? LogicForm.Expression : a.Form;
            // a choice rule: by its type name, or a request order rule:Type
            s.Rung = form == LogicForm.Expression || form == LogicForm.OrderKeys || form == LogicForm.Assignments ? RungExpression
                   : form == LogicForm.Query ? RungQuery : RungField;
            if (form == LogicForm.Rule) { RuleSite(s, s.Value); return s; }
            if (form == LogicForm.Query && SimuLean.Rules.ChoiceRules.OrderRule(s.Value) != null) { RuleSite(s, SimuLean.Rules.ChoiceRules.OrderRule(s.Value)); return s; }
            var context = form == LogicForm.Value || form == LogicForm.Query || form == LogicForm.Label ? null
                        : ExpressionContext.Of(a.Context ?? ExpressionContext.Any.Key) ?? ExpressionContext.Any;
            if (context != null) s.Context = context.Key;
            // a choice among named records (a team's / milk run's named orders): the name and what it stands for
            if (a.Role == SettingRole.ChoiceFrom && a.OptionsFrom != null && group != null)
            {
                var options = SettingsPaths.Find(type, a.OptionsFrom)?.Get(group) as IList;
                var named = options?.OfType<NamedOrderSettings>().FirstOrDefault(r => r.Name == s.Value.Trim());
                if (named != null && SimuLean.Rules.ChoiceRules.OrderRule(named.Query) != null)
                {
                    RuleSite(s, SimuLean.Rules.ChoiceRules.OrderRule(named.Query));
                    s.Value = named.Name + ": " + s.Value;
                    return s;
                }
                if (named != null) { s.Value = $"{named.Name}: {named.Query}"; s.Rung = RungQuery; QueryReads(named.Query, s); return s; }
            }
            switch (form)
            {
                case LogicForm.Expression: ExpressionReads(s.Value, context, s); break;
                case LogicForm.OrderKeys:
                    try
                    {
                        foreach (var key in ListQuerySpec.Decode("|" + s.Value).OrderKeys()) ExpressionReads(key.Key, context, s);
                    }
                    catch (FormatException e) { s.Problem = e.Message; }
                    break;
                case LogicForm.Query: QueryReads(s.Value, s); break;
                case LogicForm.Label: s.Reads.Add("item." + s.Value.Trim()); break;
                case LogicForm.Assignments:
                    foreach (object r in (IEnumerable)v)
                    {
                        string record = r as string;
                        int eq = record?.IndexOf('=') ?? -1;
                        if (eq <= 0) continue;
                        s.Writes.Add("item." + record.Substring(0, eq).Trim());
                        ExpressionReads(record.Substring(eq + 1).Trim(), context, s);
                    }
                    break;
            }
            return s;
        }

        // a rule's site: rule: Type (source hash), its declared reads; it writes nothing
        private static void RuleSite(DecisionSite s, string name)
        {
            s.Rule = (name ?? "").Trim();
            s.Rung = RungRule;
            s.Value = SimuLean.Rules.ChoiceRules.Display(s.Rule);
            var t = SimuLean.Rules.ChoiceRules.Find(s.Rule);
            if (t == null) { s.Problem = $"no choice rule '{s.Rule}'"; return; }
            foreach (string r in SimuLean.Rules.ChoiceRules.Describe(t)?.Reads ?? new string[0]) if (!s.Reads.Contains(r)) s.Reads.Add(r);
        }

        // the rule's parameters with their values (the owner's records, else the field's initial value)
        private static void RuleParameters(DecisionSite s, List<PropertySettings> records)
        {
            foreach (var p in SimuLean.Rules.ChoiceRules.Parameters(SimuLean.Rules.ChoiceRules.Find(s.Rule)))
            {
                var rec = records?.FirstOrDefault(r => r != null && (r.Name ?? "").Trim() == p.Name);
                string v = rec == null ? SimuLean.Optimization.ModelDescription.Text(p.Default)
                         : rec.Kind == PropertyValueKind.Number ? rec.Value.ToString("R", CultureInfo.InvariantCulture) : rec.Text;
                s.Parameters.Add($"{SimuLean.Rules.ChoiceRules.Key}.{p.Name} = {v}");
                if (v != SimuLean.Optimization.ModelDescription.Text(p.Default)) s.RuleAtDefaults = false;
            }
        }

        private static void ExpressionReads(string text, ExpressionContext context, DecisionSite s)
        {
            try
            {
                foreach (string r in Expression.Parse(text, context).Reads())
                {
                    if (!s.Reads.Contains(r)) s.Reads.Add(r);
                    // a model read names its object: content('Buffer'), get('M', 'p'), stat('M', 'utilization')
                    var m = QuotedName.Match(r);
                    if (m.Success && !s.Refs.Contains(m.Groups[1].Value)) s.Refs.Add(m.Groups[1].Value);
                }
            }
            catch (FormatException e) { s.Problem = s.Problem ?? e.Message; }
        }

        private static readonly System.Text.RegularExpressions.Regex QuotedName = new System.Text.RegularExpressions.Regex(@"^\w+\('([^']+)'");

        private static void QueryReads(string text, DecisionSite s)
        {
            try
            {
                var q = ListQuerySpec.Decode(text);
                var keys = q.conditions.Select(c => c.field).Concat(q.OrderKeys().Select(k => k.Key));
                foreach (string key in keys)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (ListQuerySpec.IsExpression(key)) ExpressionReads(key, ExpressionContext.ListField, s);
                    else if (!s.Reads.Contains(key.Trim())) s.Reads.Add(key.Trim());
                }
            }
            catch (FormatException e) { s.Problem = s.Problem ?? e.Message; }
        }

        /// <summary>A setting's value as text: a list of records as <c>field=value, …</c> per record, separated by <c>; </c>.</summary>
        private static string Text(object v)
        {
            if (v is IList list && !(v is string) && list.Count > 0 && list[0] is SettingsGroup)
                return string.Join("; ", list.Cast<object>().Select(r =>
                {
                    var parts = SettingsPaths.Values(r, "", false).Select(p => (p.path, text: SimuLean.Optimization.ModelDescription.Text(p.value)))
                                             .Where(p => p.text.Trim('|', ';', ' ').Length > 0).Select(p => p.path + "=" + p.text.Trim().TrimEnd('|', ';')).ToList();
                    return parts.Count == 0 ? "(defaults)" : string.Join(", ", parts);
                }));
            return SimuLean.Optimization.ModelDescription.Text(v);
        }

        // ── coverage ─────────────────────────────────────────────────────

        /// <summary>Every settings type of a model: each element type's, a team's, a reaction's and a downtime's, and the groups
        /// and records inside them.</summary>
        public static List<Type> SettingsTypes()
        {
            var seen = new List<Type>();
            void Visit(Type t)
            {
                if (t == null || seen.Contains(t)) return;
                seen.Add(t);
                foreach (var m in SettingsPaths.Members(t))
                {
                    if (m.IsGroup) Visit(m.Type);
                    else if (m.RecordType != null) Visit(m.RecordType);
                }
            }
            foreach (string type in ElementSettings.TypeNames) Visit(ElementSettings.TypeFor(type));
            Visit(typeof(ResourceTeamConfig));
            Visit(typeof(ReactionSettings));
            Visit(typeof(DowntimeSettings));
            return seen;
        }

        // a name that reads as a rule, a choice of what or of the order
        private static readonly System.Text.RegularExpressions.Regex DecisionName = new System.Text.RegularExpressions.Regex(
            "Condition|Query|Order|Sort|Priority|Strategy|Score|Filter|Rule|Policy|Expression|Applies|OnlyIf|Preempt|Acquire|MaxWait|" +
            "Timeout|Scan|Gated|SingleLabel|LabelPairs|Requirements|Signal|Formula|Weights|Routing|Release|Select|Access|Pattern|Labels$");

        /// <summary>
        /// The settings of <paramref name="settingsType"/> that look like decisions and declare no <see cref="SettingAttribute.Logic"/>
        /// (<see cref="LogicSite.Unset"/>): a choice, an expression, an option (an enum) or a name that reads as a rule
        /// (<c>…Condition</c>, <c>…Order</c>, <c>…Priority</c>, <c>…Filter</c>…). Each must be declared: a site, or
        /// <see cref="LogicSite.None"/> when it decides nothing.
        /// </summary>
        public static List<string> Undeclared(Type settingsType)
        {
            var list = new List<string>();
            foreach (var m in SettingsPaths.Members(settingsType))
            {
                if (m.IsGroup) continue;
                var a = Attr(m);
                if (a != null && a.Logic != LogicSite.Unset) continue;
                Type u = Nullable.GetUnderlyingType(m.Type) ?? m.Type;
                var role = a?.Role ?? SettingRole.Auto;
                bool looks = u.IsEnum || role == SettingRole.Expression || role == SettingRole.Choice || role == SettingRole.ChoiceFrom
                             || role == SettingRole.SamplerType || DecisionName.IsMatch(m.Field.Name);
                if (looks) list.Add(settingsType.Name + "." + m.Field.Name);
            }
            return list;
        }
    }
}
