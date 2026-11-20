using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using SimuLean.Serialization;

namespace SimuLean.Unity
{
    /// <summary>
    /// One parameter value of a choice rule in the scene: the rule's <c>[RuleParameter]</c> field name and its
    /// value as text (a number, true / false, a text or an option). Extracted as the owner's <c>rule</c> records — a number as
    /// a number (catalog <c>&lt;Element&gt;.rule.&lt;name&gt;</c>), anything else as a text. A field not listed keeps the
    /// rule's own initial value.
    /// </summary>
    [Serializable]
    public class RuleParameterValue
    {
        [Tooltip("The rule's parameter (its [RuleParameter] field name).")]
        public string name = "";
        [Tooltip("Its value: a number, true / false, a text or an option's name.")]
        public string value = "";

        /// <summary>The record of this value: a number (true / false as 1 / 0) or a text.</summary>
        public PropertySettings ToRecord()
        {
            string v = (value ?? "").Trim();
            if (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return PropertySettings.Number(name.Trim(), d);
            if (bool.TryParse(v, out bool b)) return PropertySettings.Number(name.Trim(), b ? 1 : 0);
            return PropertySettings.OfText(name.Trim(), v);
        }

        /// <summary>Adds <paramref name="values"/> to <paramref name="records"/> (a name already there is replaced); the list,
        /// created when null and something is added.</summary>
        public static List<PropertySettings> Merge(List<PropertySettings> records, IEnumerable<RuleParameterValue> values)
        {
            if (values == null) return records;
            foreach (var v in values)
            {
                if (v == null || string.IsNullOrWhiteSpace(v.name)) continue;
                var rec = v.ToRecord();
                if (records == null) records = new List<PropertySettings>();
                records.RemoveAll(r => r != null && (r.Name ?? "").Trim() == rec.Name);
                records.Add(rec);
            }
            return records;
        }

        /// <summary>Adds <paramref name="values"/> to the element's <c>rule</c> records.</summary>
        public static void Extract(ElementConfig config, IEnumerable<RuleParameterValue> values)
        {
            if (config == null) return;
            var merged = Merge(config.Get<List<PropertySettings>>(SimuLean.Rules.ChoiceRules.Key, null), values);
            if (merged != null && merged.Count > 0) config.Set(SimuLean.Rules.ChoiceRules.Key, merged);
        }
    }
    /// <summary>
    /// Rule parameters that change nothing: a parameter list set while its rule is empty
    /// (the extractor drops it) or naming a parameter its rule does not have. A Queue has two lists — <c>ruleParameters</c>
    /// for its release rule (<c>offerRule</c>) and <c>outputStrategy.ruleParameters</c> for its routing rule — so a name
    /// that belongs to the other list's rule says which list was meant. Read by <c>simulean_set</c> / <c>simulean_apply</c>
    /// and logged by the scene extraction (<c>simulean_validate</c>); a team's list without a dispatch rule is already a
    /// build error.
    /// </summary>
    public static class RuleParameterChecks
    {
        private const string RoutingList = "outputStrategy.ruleParameters", RoutingRule = "outputStrategy.rule (with outputStrategy.strategyType = Rule)";

        /// <summary>The warnings for <paramref name="c"/>'s rule parameter lists (empty when none).</summary>
        public static List<string> Problems(Component c, bool includeTeams = true)
        {
            var w = new List<string>();
            if (c is UnityResourceTeam team)
            {
                if (includeTeams)
                    Site(w, team.name, "ruleParameters", "requestOrderName (rule:<Rule>)", SimuLean.Rules.ChoiceRules.OrderRule(team.requestOrderName) ?? "",
                         team.ruleParameters, null, null);
                return w;
            }
            if (!(c is SElement e)) return w;
            var os = e.outputStrategy;
            string routing = os != null && os.strategyType == OutputStrategyType.Rule ? (os.rule ?? "").Trim() : "";
            if (e is UnityQueue q)
                Site(w, e.name, "ruleParameters", "offerRule", (q.offerRule ?? "").Trim(), q.ruleParameters, RoutingList, routing);
            if (os != null && e.HasOutput)
                Site(w, e.name, RoutingList, RoutingRule, routing, os.ruleParameters,
                     e is UnityQueue ? "ruleParameters" : null, e is UnityQueue q2 ? (q2.offerRule ?? "").Trim() : null);
            return w;
        }

        private static void Site(List<string> w, string owner, string listKey, string ruleKey, string rule, List<RuleParameterValue> values,
                                 string otherList, string otherRule)
        {
            var names = values == null ? new List<string>() : values.Where(v => v != null && !string.IsNullOrWhiteSpace(v.name)).Select(v => v.name.Trim()).ToList();
            if (names.Count == 0) return;
            var otherType = string.IsNullOrEmpty(otherRule) ? null : SimuLean.Rules.ChoiceRules.Find(otherRule);
            var otherNames = otherType == null ? new List<string>() : SimuLean.Rules.ChoiceRules.Parameters(otherType).Select(p => p.Name).ToList();
            if (rule.Length == 0)
            {
                var theirs = names.Where(otherNames.Contains).ToList();
                string hint = theirs.Count > 0
                    ? $" {string.Join(", ", theirs)} {(theirs.Count == 1 ? "is a parameter" : "are parameters")} of {otherType.Name}: set {(theirs.Count == 1 ? "it" : "them")} in {otherList}."
                    : otherList != null ? $" (A Queue has two lists: ruleParameters for offerRule, {RoutingList} for the routing rule.)" : "";
                w.Add($"'{owner}'.{listKey}: {string.Join(", ", names)} set, but {ruleKey} is empty: {(names.Count == 1 ? "it is" : "they are")} not used." + hint);
                return;
            }
            var type = SimuLean.Rules.ChoiceRules.Find(rule);
            if (type == null) return;   // the rule itself is reported (not found / not compiled)
            var own = SimuLean.Rules.ChoiceRules.Parameters(type).Select(p => p.Name).ToList();
            foreach (string n in names)
            {
                if (own.Contains(n)) continue;
                string hint = otherNames.Contains(n) ? $" '{n}' is a parameter of {otherType.Name}: did you mean {otherList}? (A Queue's two lists share their values, so it applies there too; keep it in its own list.)"
                            : SimuLean.Serialization.NameSuggestions.DidYouMean(n, own) + (own.Count > 0 ? $" Its parameters: {string.Join(", ", own)}." : " It has no parameters.");
                w.Add($"'{owner}'.{listKey}: rule {type.Name} has no parameter '{n}'." + hint);
            }
        }
    }
}
