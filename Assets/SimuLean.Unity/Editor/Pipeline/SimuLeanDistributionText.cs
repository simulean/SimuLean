using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Serialization;
using UnityEditor;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// A distribution field (<see cref="RandomProcessConfig"/>) set by text through <c>simulean_set</c> /
    /// <c>simulean_apply</c>: the <see cref="SamplerSpec"/> form <c>Type~p1~p2…</c>
    /// (<c>Triangular~0.6~1~1.6</c>, <c>ExponentialMean~5</c>, <c>Expression~item.t * 2</c>) or a plain number (a constant)
    /// sets the type and its typed fields. <c>ExponentialMean~m</c> is an input form only: it is stored as
    /// <c>Exponential</c> with rate 1 / m (the field keeps a rate).
    /// </summary>
    public static partial class SimuLeanCommands
    {
        /// <summary>Every distribution's keys, one line per type (what <c>simulean_types</c> / <c>simulean_params</c> show
        /// once, and the error of a distribution text names).</summary>
        internal static Dictionary<string, string> DistributionKeys()
        {
            var keys = new Dictionary<string, string>();
            foreach (var kv in SamplerSpec.Fields)
                keys[kv.Key] = kv.Key == "ExponentialMean"
                    ? "text only: ExponentialMean~mean (stored as Exponential, exponentialRate = 1 / mean)"
                    : string.Join(", ", kv.Value);
            keys["Expression"] = "expression";
            return keys;
        }

        internal const string DistributionTextHelp =
            "a distribution is set as a whole by text 'Type~p1~p2…' (e.g. 'Triangular~0.6~1~1.6', 'ExponentialMean~5', " +
            "'Exponential~0.2' (a rate), 'Uniform~1~3', 'Expression~item.t * 2') or a number (a constant), or field by field " +
            "('<field>.strategyType' then its keys)";

        /// <summary>True when <paramref name="p"/> is a distribution (a <see cref="RandomProcessConfig"/>).</summary>
        internal static bool IsDistribution(SerializedProperty p)
            => p != null && p.propertyType == SerializedPropertyType.Generic && !p.isArray
               && p.FindPropertyRelative("strategyType") is SerializedProperty t && t.propertyType == SerializedPropertyType.Enum
               && p.FindPropertyRelative("constantValue") != null;

        /// <summary>Sets a distribution from its text: the type and the type's fields (the others keep their values).</summary>
        internal static void AssignDistribution(SerializedProperty p, string value)
        {
            string text = (value ?? "").Trim();
            SamplerSettings s;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double constant))
                s = new SamplerSettings { StrategyType = "Constant", ConstantValue = constant };
            else
                try { s = SamplerSpec.Parse(text); }
                catch (FormatException ex)
                {
                    throw new ArgumentException($"'{Friendly(p.propertyPath)}': {ex.Message} — {DistributionTextHelp}. Keys per type: " +
                                                string.Join("; ", DistributionKeys().Select(k => k.Key + ": " + k.Value)) + ".");
                }
            string type = s.StrategyType;
            if (type == "ExponentialMean")
            {
                double mean = s.ExponentialMean ?? 0;
                if (!(mean > 0)) throw new ArgumentException($"'{Friendly(p.propertyPath)}': ExponentialMean needs a mean > 0 (e.g. 'ExponentialMean~5').");
                s = new SamplerSettings { StrategyType = "Exponential", ExponentialRate = 1.0 / mean };
                type = "Exponential";
            }
            var strategy = p.FindPropertyRelative("strategyType");
            int index = Array.IndexOf(strategy.enumNames, type);
            if (index < 0) throw new ArgumentException($"'{Friendly(p.propertyPath)}': '{type}' is not a distribution of this field ({string.Join(", ", strategy.enumNames)}).");
            strategy.enumValueIndex = index;
            if (type == "Expression")
            {
                p.FindPropertyRelative("expression").stringValue = s.Expression ?? "";
                return;
            }
            if (!SamplerSpec.Fields.TryGetValue(type, out var fields)) return;
            foreach (string f in fields)
            {
                var q = p.FindPropertyRelative(f);
                object v = SettingsPaths.Find(typeof(SamplerSettings), f)?.Get(s);
                if (q == null || v == null) continue;
                switch (q.propertyType)
                {
                    case SerializedPropertyType.Integer: q.longValue = Convert.ToInt64(v, CultureInfo.InvariantCulture); break;
                    case SerializedPropertyType.Float: q.doubleValue = Convert.ToDouble(v, CultureInfo.InvariantCulture); break;
                    case SerializedPropertyType.Boolean: q.boolValue = Convert.ToBoolean(v, CultureInfo.InvariantCulture); break;
                }
            }
        }

        /// <summary>A distribution's text as <c>simulean_set</c> takes it: <c>Type~p1~p2</c> (its type's fields in the text
        /// order, <see cref="SamplerSpec.Fields"/>), <c>Expression~text</c>.</summary>
        internal static string DistributionText(SerializedProperty p)
        {
            var strategy = p.FindPropertyRelative("strategyType");
            int i = strategy.enumValueIndex;
            string type = i >= 0 && i < strategy.enumNames.Length ? strategy.enumNames[i] : "Constant";
            if (type == "Expression") return "Expression~" + (p.FindPropertyRelative("expression")?.stringValue ?? "");
            var parts = new List<string> { type };
            if (SamplerSpec.Fields.TryGetValue(type, out var fields))
                foreach (string f in fields)
                {
                    var q = p.FindPropertyRelative(f);
                    if (q == null) break;
                    parts.Add(ValueOf(q));
                }
            return string.Join("~", parts);
        }

        /// <summary>A serialized path as the user writes it: <c>actions[0].kind</c>, not Unity's
        /// <c>actions.Array.data[0].kind</c>.</summary>
        internal static string Friendly(string path) => (path ?? "").Replace(".Array.data[", "[");
    }
}
