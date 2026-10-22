using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SimuLean.Expressions;
using SimuLean.Optimization;
using SimuLean.Results;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// What a study of the MCP can say beyond factors and metrics (the agents
    /// wrote these as C# scripts): explicit <c>scenarios</c>, a <c>where</c> condition that drops factor combinations,
    /// <c>derived</c> metrics computed per replication from the others, the <c>confidence</c> level, a <c>warmup</c>, and
    /// metrics split by a label value (<c>tally:x[*]:stat</c>). Applied to the study before it runs
    /// (<see cref="Apply"/>) and to its results after (<see cref="Finish"/>).
    /// </summary>
    internal sealed class StudyOptions
    {
        public string Scenarios = "", Where = "", Derived = "";
        public double Confidence, Warmup = -1;

        /// <summary>The metrics with a <c>[*]</c> split, expanded after the runs: (name, path with the wildcard).</summary>
        public readonly List<(string name, string path)> Wildcards = new List<(string, string)>();
        private readonly List<(string name, Expression expr, string text)> derived = new List<(string, Expression, string)>();

        /// <summary>A factor path as an expression name: every character that is not a letter, a digit or _ becomes _
        /// (<c>Buf1.capacity</c> → <c>Buf1_capacity</c>, <c>team:Welders.members</c> → <c>team_Welders_members</c>).</summary>
        public static string NameOf(string path)
        {
            var sb = new StringBuilder();
            foreach (char c in path.Trim()) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        /// <summary>Adds the metrics of <paramref name="metrics"/> (a wildcard one is added unsplit, so its path is checked,
        /// and expanded after the runs).</summary>
        public void AddMetrics(Study study, List<(string name, string path)> metrics)
        {
            foreach (var (name, path) in metrics)
            {
                if (path.Contains("[*]"))
                {
                    Wildcards.Add((name, path));
                    study.Metric(name, path.Replace("[*]", ""));
                }
                else study.Metric(name, path);
            }
        }

        /// <summary>Before the runs: the explicit scenarios (their paths become the factors), the <c>where</c> filter on the
        /// factor combinations, the confidence level, the warm-up, the derived metrics parsed and checked.</summary>
        public void Apply(Study study, bool hasSet)
        {
            if (Confidence > 0)
            {
                if (Confidence >= 1) throw new ArgumentException($"confidence: {Confidence} is not between 0 and 1 (e.g. 0.95).");
                study.Confidence = Confidence;
            }
            if (Warmup >= 0) study.Model.WarmupTime = Warmup;
            if (!string.IsNullOrWhiteSpace(Scenarios))
            {
                if (hasSet) throw new ArgumentException("scenarios: give the factors in the scenarios or in set, not both.");
                AddScenarios(study);
            }
            if (!string.IsNullOrWhiteSpace(Where)) Filter(study);
            ParseDerived(study);
        }

        // [{"name": "A", "Buf1.capacity": 2, "replications": 5}, …] or {"A": {"Buf1.capacity": 2}, …}
        private void AddScenarios(Study study)
        {
            JToken json;
            try { json = JToken.Parse(Scenarios.Trim()); }
            catch (Newtonsoft.Json.JsonException e)
            { throw new ArgumentException($"scenarios: not valid JSON ({e.Message}). Expected e.g. [{{\"name\": \"A\", \"Buffer.capacity\": 2}}]."); }
            var list = new List<(string name, int reps, Dictionary<string, string> values)>();
            if (json is JArray a)
                foreach (var item in a)
                {
                    if (!(item is JObject o)) throw new ArgumentException("scenarios: each item is an object of factor paths and values.");
                    list.Add(Scenario((string)o["name"] ?? $"S{list.Count + 1}", o));
                }
            else if (json is JObject byName)
                foreach (var kv in byName)
                {
                    if (!(kv.Value is JObject o)) throw new ArgumentException($"scenarios: '{kv.Key}' is not an object of factor paths and values.");
                    list.Add(Scenario(kv.Key, o));
                }
            if (list.Count == 0) throw new ArgumentException("scenarios: none given.");
            var paths = new List<string>();
            foreach (var s in list) foreach (string p in s.values.Keys) if (!paths.Contains(p)) paths.Add(p);
            foreach (var s in list)
            {
                var missing = paths.Where(p => !s.values.ContainsKey(p)).ToList();
                if (missing.Count > 0) throw new ArgumentException($"scenarios: '{s.name}' gives no value for {string.Join(", ", missing)} (every scenario gives every factor).");
            }
            foreach (string p in paths) study.Factor(p, list.Select(s => s.values[p]).Distinct().ToArray());
            foreach (var s in list) study.Scenario(s.name, s.reps, paths.Select(p => s.values[p]).ToArray());
        }

        private static (string, int, Dictionary<string, string>) Scenario(string name, JObject o)
        {
            var values = new Dictionary<string, string>();
            int reps = 0;
            foreach (var kv in o)
            {
                if (kv.Key == "name") continue;
                if (kv.Key == "replications") { reps = (int)kv.Value; continue; }
                switch (kv.Value.Type)
                {
                    case JTokenType.Integer:
                    case JTokenType.Float: values[kv.Key] = ((double)kv.Value).ToString("R", CultureInfo.InvariantCulture); break;
                    case JTokenType.Boolean: values[kv.Key] = (bool)kv.Value ? "true" : "false"; break;
                    case JTokenType.String: values[kv.Key] = (string)kv.Value; break;
                    default: throw new ArgumentException($"scenarios: '{name}': '{kv.Key}' is a number, a text or true / false.");
                }
            }
            return (name, reps, values);
        }

        // keeps the factor combinations for which the condition holds, as explicit scenarios
        private void Filter(Study study)
        {
            var labels = study.FactorLabels;
            if (labels.Count == 0) throw new ArgumentException("where: it filters the combinations of the factors of set; give set.");
            Expression condition;
            try { condition = Expression.Parse(Where); }
            catch (Exception e) { throw new ArgumentException($"where: {e.Message}"); }
            var names = labels.Select(NameOf).ToList();
            var plan = study.Plan();
            var kept = new List<StudyScenario>();
            foreach (var s in plan)
            {
                var scope = new Scope();
                for (int i = 0; i < names.Count; i++) scope.Set(names[i], s.Values[i]);
                bool ok;
                try { ok = condition.Condition(scope); }
                catch (Exception e) { throw new ArgumentException($"where: {e.Message}"); }
                if (scope.Missing.Count > 0)
                    throw new ArgumentException($"where: unknown name {string.Join(", ", scope.Missing.Distinct())} (the factors are {string.Join(", ", names)}).");
                if (ok) kept.Add(s);
            }
            if (kept.Count == 0) throw new ArgumentException($"where: no combination of the {plan.Count} meets '{Where}'.");
            foreach (var s in kept) study.Scenario(s.Name, s.Replications, s.Values.ToArray());
        }

        private void ParseDerived(Study study)
        {
            derived.Clear();
            if (string.IsNullOrWhiteSpace(Derived)) return;
            var known = new HashSet<string>(study.Metrics.Select(m => m.Name));
            foreach (var (name, text) in SimuLeanCommands.MetricsOf(Derived))
            {
                if (known.Contains(name)) throw new ArgumentException($"derived: '{name}' is already a metric.");
                Expression e;
                try { e = Expression.Parse(text); }
                catch (Exception ex) { throw new ArgumentException($"derived '{name}': {ex.Message}"); }
                var scope = new Scope();
                foreach (string k in known) scope.Set(k, 1.0);
                try { e.Number(scope); } catch (Exception) { /* a value of 1 everywhere may divide by zero: only names are checked */ }
                if (scope.Missing.Count > 0)
                    throw new ArgumentException($"derived '{name}': unknown name {string.Join(", ", scope.Missing.Distinct())} (the metrics are " +
                                                $"{string.Join(", ", known)}; derived metrics may use those before them).");
                derived.Add((name, e, text));
                known.Add(name);
            }
        }

        /// <summary>After the runs: the wildcard metrics expanded per label value (<c>name[value]</c>), then the derived
        /// metrics of each completed run.</summary>
        public void Finish(ResultSet set)
        {
            foreach (var (name, path) in Wildcards) Expand(set, name, path);
            foreach (var (name, expr, _) in derived)
            {
                set.MetricNames.Add(name);
                foreach (var run in set.Runs.Where(r => r.Error == null))
                {
                    var scope = new Scope();
                    foreach (var kv in run.Metrics) scope.Set(kv.Key, kv.Value);
                    double v;
                    try { v = expr.Number(scope); } catch (Exception) { v = double.NaN; }
                    run.Metrics[name] = v;
                }
            }
        }

        private static void Expand(ResultSet set, string name, string path)
        {
            var metric = Study.MetricOf(name, path.Replace("[*]", "[]"));
            string statistic = metric.Statistic ?? "";
            int at = statistic.IndexOf("[]", StringComparison.Ordinal);
            if (at < 0) return;
            string prefix = statistic.Substring(0, at + 1), suffix = statistic.Substring(at + 1);
            var scope = metric.Scope == MetricScope.Team ? ResultScope.Team : metric.Scope == MetricScope.List ? ResultScope.List
                      : metric.Scope == MetricScope.Model ? ResultScope.Model : metric.Scope == MetricScope.Reaction ? ResultScope.Reaction
                      : ResultScope.Element;
            var names = new List<string>();
            foreach (var run in set.Runs.Where(r => r.Error == null && r.Results != null))
            {
                var o = run.Results.Find(scope, scope == ResultScope.Model ? "model" : metric.Target);
                if (o == null) continue;
                foreach (var kv in o.Metrics)
                {
                    if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal) || !kv.Key.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    if (kv.Key.Length <= prefix.Length + suffix.Length) continue;
                    string value = kv.Key.Substring(prefix.Length, kv.Key.Length - prefix.Length - suffix.Length);
                    string key = $"{name}[{value}]";
                    run.Metrics[key] = kv.Value;
                    if (!names.Contains(key)) names.Add(key);
                }
            }
            names.Sort(NaturalOrder);
            set.MetricNames.AddRange(names.Where(n => !set.MetricNames.Contains(n)));
        }

        // "x[2]" before "x[10]"
        private static int NaturalOrder(string a, string b)
        {
            string va = a.Substring(a.IndexOf('[') + 1).TrimEnd(']'), vb = b.Substring(b.IndexOf('[') + 1).TrimEnd(']');
            bool na = double.TryParse(va, NumberStyles.Float, CultureInfo.InvariantCulture, out double x);
            bool nb = double.TryParse(vb, NumberStyles.Float, CultureInfo.InvariantCulture, out double y);
            return na && nb ? x.CompareTo(y) : string.CompareOrdinal(a, b);
        }

        /// <summary>Names → values for an expression; names it was asked for and did not have are kept.</summary>
        private sealed class Scope : IExpressionScope
        {
            private readonly Dictionary<string, ExprValue> values = new Dictionary<string, ExprValue>();
            public readonly List<string> Missing = new List<string>();
            public double Now => 0;

            public void Set(string name, double v) => values[name] = ExprValue.Of(v);
            public void Set(string name, string text)
                => values[name] = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? ExprValue.Of(d) : ExprValue.Of(text);

            public ExprValue Get(string root, string name)
            {
                string key = root == null ? name : root + "." + name;
                if (values.TryGetValue(key, out var v)) return v;
                Missing.Add(key);
                return ExprValue.None;
            }
        }
    }
}
