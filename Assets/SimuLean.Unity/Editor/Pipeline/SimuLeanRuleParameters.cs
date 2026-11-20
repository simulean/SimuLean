using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Rules;
using SimuLean.Serialization;
using UnityEditor;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// A choice rule's parameters through <c>simulean_set</c> / <c>simulean_apply</c>: setting
    /// a rule (<c>outputStrategy.rule</c>, a Queue's <c>offerRule</c>, a team's <c>requestOrderName = rule:X</c>) fills its
    /// <c>ruleParameters</c> with every <c>[RuleParameter]</c> field missing there, at its initial value, and lists them;
    /// <c>ruleParameters</c> takes <c>Name = value, …</c> text (each named parameter set or added; empty = none listed).
    /// </summary>
    public static partial class SimuLeanCommands
    {
        internal static bool IsRuleParameterList(SerializedProperty p)
            => p != null && p.isArray && p.propertyType != SerializedPropertyType.String && p.arrayElementType == nameof(RuleParameterValue);

        internal static void AssignRuleParameters(SerializedProperty list, string value)
        {
            var parts = SplitList(value);
            if (parts.Count == 0) { list.arraySize = 0; return; }
            foreach (string part in parts)
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    throw new ArgumentException($"'{Friendly(list.propertyPath)}': '{part}' is not 'Name = value' (e.g. 'FreeWeight = 0.5, DueLabel = due').");
                string name = part.Substring(0, eq).Trim(), v = part.Substring(eq + 1).Trim().Trim('\'', '"');
                int index = IndexOfParameter(list, name);
                if (index < 0) { index = list.arraySize; list.arraySize++; }
                var item = list.GetArrayElementAtIndex(index);
                item.FindPropertyRelative("name").stringValue = name;
                item.FindPropertyRelative("value").stringValue = v;
            }
        }

        private static int IndexOfParameter(SerializedProperty list, string name)
        {
            for (int i = 0; i < list.arraySize; i++)
                if (string.Equals(list.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue?.Trim(), name, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>After <paramref name="path"/> was set: when it names a rule (or is its parameter list), the rule's
        /// parameters as <c>Name = value</c> (missing ones added at their initial values), with warnings for names the rule
        /// does not have. Null when the path is not a rule's.</summary>
        internal static List<string> FillRuleParameters(SerializedObject so, string path, List<string> warnings)
        {
            string prefix, rule;
            if (path.EndsWith("ruleParameters", StringComparison.Ordinal)) prefix = path.Substring(0, path.Length - "ruleParameters".Length);
            else if (path == "offerRule" || path.EndsWith(".rule", StringComparison.Ordinal) || path == "requestOrderName")
                prefix = path.Substring(0, path.Length - (path.EndsWith(".rule", StringComparison.Ordinal) ? "rule" : path).Length);
            else return null;
            var list = so.FindProperty(prefix + "ruleParameters");
            if (list == null || !IsRuleParameterList(list)) return null;
            var ruleProp = so.FindProperty(prefix + "rule") ?? so.FindProperty("offerRule") ?? so.FindProperty("requestOrderName");
            rule = ruleProp?.stringValue?.Trim() ?? "";
            if (rule.StartsWith(ChoiceRules.OrderPrefix, StringComparison.Ordinal)) rule = rule.Substring(ChoiceRules.OrderPrefix.Length).Trim();
            else if (ruleProp != null && ruleProp.name == "requestOrderName") rule = "";   // a named order, not a rule
            if (rule.Length == 0) return null;
            var type = ChoiceRules.Find(rule);
            if (type == null)
            {
                warnings.Add($"Rule '{rule}' is not found (not compiled yet?): its parameters are not filled in. simulean_rule_show lists the rules.");
                return null;
            }
            var parameters = ChoiceRules.Parameters(type);
            foreach (var p in parameters)
            {
                if (IndexOfParameter(list, p.Name) >= 0) continue;
                list.arraySize++;
                var item = list.GetArrayElementAtIndex(list.arraySize - 1);
                item.FindPropertyRelative("name").stringValue = p.Name;
                item.FindPropertyRelative("value").stringValue = ParameterText(p.Default);
            }
            // names the rule does not have: RuleParameterChecks, once the edit is applied (with the other list's did-you-mean)
            var result = new List<string>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var item = list.GetArrayElementAtIndex(i);
                result.Add((item.FindPropertyRelative("name").stringValue?.Trim() ?? "") + " = " + item.FindPropertyRelative("value").stringValue);
            }
            return result;
        }

        /// <summary>True when setting <paramref name="path"/> can change what a rule parameter list means: the list itself,
        /// a rule (<c>offerRule</c>, <c>outputStrategy.rule</c>, <c>requestOrderName</c>) or the routing type.</summary>
        internal static bool IsRuleSitePath(string path)
            => path != null && (path.Contains("ruleParameters") || path == "offerRule" || path == "requestOrderName"
                                || path == "outputStrategy.rule" || path == "outputStrategy.strategyType");

        private static string ParameterText(object v)
        {
            switch (v)
            {
                case null: return "";
                case bool b: return b ? "true" : "false";
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return v.ToString();
            }
        }
    }
}
