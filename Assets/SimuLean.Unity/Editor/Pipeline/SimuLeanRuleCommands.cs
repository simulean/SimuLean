using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimuLean.Rules;
using SimuLean.Serialization;
using Unity.Pipeline.Commands;
using UnityEditor;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The choice-rule commands: write a rule from a template (<c>simulean_rule_new</c>), check it against the
    /// contract and a model (<c>simulean_rule_check</c>) and show it with the elements that use it (<c>simulean_rule_show</c>).
    /// A rule is the last step of the escalation field → expression → query / order → reaction → rule: every one carries a
    /// <c>Why</c> naming the declarative form tried.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        /// <summary>The folder of the rules (the assembly <c>SimuLean.UserLogic</c>).</summary>
        public const string RulesFolder = "Assets/SimuLean.UserLogic";

        [CliCommand("simulean_rule_new", "Write a choice rule from a template: kind routing (scores each next element), release (scores " +
                                         "each item a Queue holds) or dispatch (scores each pending request of a team); name = its class " +
                                         "name; why = the declarative form tried first (BestScore expression, offerSort, a request order, a " +
                                         "reaction) and why it is not enough — refused without it. Writes Assets/SimuLean.UserLogic/<name>.cs " +
                                         "and starts a recompile: edit its Score, then simulean_rule_check.", Tags = new[] { Tag })]
        public static object RuleNew(
            [CliArg("kind", "The site: routing, release or dispatch.")] string kind = "",
            [CliArg("name", "The rule's class name (a C# identifier, e.g. NearestFreeCell).")] string name = "",
            [CliArg("why", "The declarative form tried first and why it is not enough (required).")] string why = "")
            => AgentReply.Of(() =>
            {
                string source = RuleTemplate(kind, name, why);
                string path = RulesFolder + "/" + name.Trim() + ".cs";
                if (File.Exists(path)) throw new ArgumentException($"name: {path} exists already (edit it, or choose another name).");
                if (ChoiceRules.Find(name.Trim()) != null) throw new ArgumentException($"name: a rule '{name.Trim()}' exists already ({ChoiceRules.Find(name.Trim()).FullName}).");
                Directory.CreateDirectory(RulesFolder);
                File.WriteAllText(path, source);
                AssetDatabase.ImportAsset(path);
                return new Dictionary<string, object>
                {
                    ["file"] = path, ["site"] = Site(kind).ToString().ToLowerInvariant(), ["name"] = name.Trim(),
                    ["next"] = "Unity recompiles the scripts: edit Score in the file, then simulean_rule_check name=" + name.Trim() +
                               " (and set the rule on the element: " + SettingOf(Site(kind)) + ").",
                };
            });

        [CliCommand("simulean_rule_check", "Check a choice rule: found after the last compile, its site, the contract scan (no static " +
                                           "state, no System.Random, threads or I/O), its parameters, source hash and Why; with object, the " +
                                           "element / team that uses it in the scene's model: the model builds with it, and a run of " +
                                           "horizon (-1 = the model's) twice with one seed makes the same decisions (counted).", Tags = new[] { Tag })]
        public static object RuleCheck(
            [CliArg("name", "The rule's class name.")] string name = "",
            [CliArg("object", "An element / team using it (empty = the rule alone).")] string @object = "",
            [CliArg("horizon", "The check run's length (-1 = the model's).")] double horizon = -1,
            [CliArg("seed", "The check run's seed (-1 = 1).")] int seed = -1)
            => AgentReply.Of(() =>
            {
                var d = RuleData(name);
                if (string.IsNullOrWhiteSpace(@object)) return d;
                var model = SceneStudy.Of(null).Model;
                string owner = @object.Trim();
                var users = Users(model, name.Trim());
                if (!users.Any(u => (string)u["owner"] == owner))
                    throw new ArgumentException($"object: '{owner}' does not use the rule '{name.Trim()}'" +
                                                (users.Count == 0 ? " (nothing in the scene's model does)." : " (used by " + string.Join(", ", users.Select(u => u["owner"])) + ")."));
                double h = horizon > 0 ? horizon : model.MaxSimulationTime;
                long Run()
                {
                    var f = new SimuLean.Headless.HeadlessModelFactory(new SimClock(seed > 0 ? seed : 1), enableLogging: false);
                    f.BuildModel(ConfigCloner.DeepClone(model));
                    f.StartModel();
                    f.Clock.AdvanceClock(h);
                    var site = SiteOf(f, owner);
                    return site?.Decisions ?? 0;
                }
                long first = Run(), second = Run();
                d["object"] = owner;
                d["horizon"] = h;
                d["decisions"] = first;
                d["deterministic"] = first == second;
                if (first == 0) d["note"] = "the rule made no decision in the run: is the element reached, and does it have a choice to make?";
                return d;
            });

        [CliCommand("simulean_rule_show", "Show a choice rule: its site, label, Why, declared reads, parameters (type, default, range; " +
                                          "catalog keys <Element>.rule.<Field>), source file and hash, and the elements / teams of the " +
                                          "scene's model that use it with their parameter values. Empty name = every rule.", Tags = new[] { Tag })]
        public static object RuleShow([CliArg("name", "The rule's class name (empty = list every rule).")] string name = "")
            => AgentReply.Of(() =>
            {
                SimulationConfig model = null;
                try { model = SceneStudy.Of(null).Model; } catch (Exception) { /* no model in the scene: the rule alone */ }
                if (string.IsNullOrWhiteSpace(name))
                    return new Dictionary<string, object>
                    {
                        ["rules"] = ChoiceRules.All.Select(t => (object)new Dictionary<string, object>
                        {
                            ["name"] = t.Name, ["site"] = string.Join(", ", ChoiceRules.SitesOf(t).Select(s => s.ToString().ToLowerInvariant())),
                            ["label"] = ChoiceRules.Describe(t)?.Label ?? t.Name,
                        }).ToList(),
                    };
                var d = RuleData(name);
                if (model != null) d["usedBy"] = Users(model, name.Trim());
                return d;
            });

        // ── helpers ──────────────────────────────────────────────────────────

        private static RuleSite Site(string kind)
        {
            switch ((kind ?? "").Trim().ToLowerInvariant())
            {
                case "routing": return RuleSite.Routing;
                case "release": return RuleSite.Release;
                case "dispatch": return RuleSite.Dispatch;
            }
            throw new ArgumentException($"kind: '{kind}' is not a rule site (routing, release or dispatch).");
        }

        private static string SettingOf(RuleSite site)
            => site == RuleSite.Routing ? "outputStrategy.strategyType = Rule, outputStrategy.rule = <name>"
             : site == RuleSite.Release ? "a Queue's offerRule = <name>"
             : "a team's requestOrderName = rule:<name>";

        /// <summary>The source of a new rule (<c>simulean_rule_new</c>): the template of the site with the name and Why.</summary>
        public static string RuleTemplate(string kind, string name, string why)
        {
            var site = Site(kind);
            name = (name ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new ArgumentException($"name: '{name}' is not a C# class name.");
            if (string.IsNullOrWhiteSpace(why) || why.Trim().Length < 20)
                throw new ArgumentException("why: say which declarative form was tried first (a BestScore expression, an offerSort, a " +
                                            "request order, a reaction) and why it is not enough; a rule is the last step.");
            string w = why.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
            string iface, args, body, alt;
            switch (site)
            {
                case RuleSite.Routing:
                    iface = "IRoutingRule"; args = "in ChoiceContext context, in ElementView output";
                    body = "-output.Content + Weight * output.Free";
                    alt = "outputStrategy.strategyType = BestScore with a scoreExpression over out.x / item.x";
                    break;
                case RuleSite.Release:
                    iface = "IReleaseRule"; args = "in ChoiceContext context, in ItemView item";
                    body = "-item.Get(\"due\") + Weight * item.Age";
                    alt = "the Queue's offerSort (order keys over labels and expressions)";
                    break;
                default:
                    iface = "IDispatchRule"; args = "in ChoiceContext context, in RequestView request";
                    body = "request.Priority + Weight * request.Age";
                    alt = "the team's request order (a query) or its named orders";
                    break;
            }
            return
$@"using SimuLean.Rules;

namespace SimuLean.UserLogic
{{
    // A {site.ToString().ToLowerInvariant()} rule. Try first: {alt}.
    // The contract: Score reads the context and the candidate only; the highest score wins, ties keep the candidates' order.
    // No writes, no static state, no System.Random (use context.Random), no Unity, no I/O. Instance fields are memory within a run.
    [ChoiceRule(""{name}"", Why = ""{w}"")]
    public sealed class {name} : {iface}
    {{
        /// <summary>A parameter: catalog key <Element>.rule.Weight (the optimiser and the Experimenter vary it).</summary>
        [RuleParameter(""Weight"", Min = 0)] public double Weight = 0;

        public double Score({args})
            => {body};
    }}
}}
";
        }

        private static Dictionary<string, object> RuleData(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name: the rule's class name.");
            var t = ChoiceRules.Find(name.Trim());
            if (t == null)
                throw new ArgumentException($"name: no choice rule '{name.Trim()}' after the last compile (is it compiled? are there compile errors?)." +
                                            NameSuggestions.DidYouMean(name.Trim(), ChoiceRules.All.Select(x => x.Name)));
            var a = ChoiceRules.Describe(t);
            string file = ChoiceRules.SourceFile(t);
            var d = new Dictionary<string, object>
            {
                ["name"] = t.Name, ["type"] = t.FullName,
                ["site"] = string.Join(", ", ChoiceRules.SitesOf(t).Select(s => s.ToString().ToLowerInvariant())),
                ["label"] = a?.Label ?? t.Name,
                ["why"] = a?.Why ?? "(none: give the rule a [ChoiceRule(Why = …)] naming the declarative form tried)",
                ["parameters"] = ChoiceRules.Parameters(t).Select(p => (object)new Dictionary<string, object>
                {
                    ["name"] = p.Name, ["key"] = ChoiceRules.Key + "." + p.Name, ["label"] = p.Label, ["type"] = p.Type.Name,
                    ["default"] = SimuLean.Optimization.ModelDescription.Text(p.Default), ["catalog"] = p.IsNumber,
                    ["min"] = double.IsNaN(p.Min) ? null : (object)p.Min, ["max"] = double.IsNaN(p.Max) ? null : (object)p.Max,
                }).ToList(),
                ["contract"] = ChoiceRules.ContractProblems(t),
                ["ok"] = ChoiceRules.ContractProblems(t).Count == 0,
            };
            if (a?.Reads != null) d["reads"] = a.Reads;
            if (file != null) { d["file"] = file.Replace('\\', '/'); d["sourceHash"] = ChoiceRules.SourceHash(t); }
            return d;
        }

        // the owners (elements, teams) whose decision sites name the rule (the model card's scripts list every rule's uses
        // the same way: LogicReport.RuleUse)
        private static List<Dictionary<string, object>> Users(SimulationConfig model, string name)
            => SimuLean.Logic.ModelLogic.Of(model).RuleSites(name).Select(x => SimuLean.Logic.LogicReport.RuleUse(x.Owner, x.Site)).ToList();

        // the rule site an owner has in a built model (its routing, its release order, its team's request order)
        private static RuleSiteBase SiteOf(SimuLean.Headless.HeadlessModelFactory f, string owner)
        {
            var e = f.Find(owner);
            if (e != null)
            {
                if (e.GetOutputStrategy() is RuleSiteBase r) return r;
                if (e is ItemsQueue q && q.OfferSort is RuleSiteBase o) return o;
            }
            return f.Clock.Context.FindTeam(owner)?.RequestRule;
        }
    }
}
