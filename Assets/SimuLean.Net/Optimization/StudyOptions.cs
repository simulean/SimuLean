using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SimuLean.Data;
using SimuLean.Expressions;
using SimuLean.Results;

namespace SimuLean.Optimization
{
    /// <summary>
    /// What a study can say beyond factors and metrics: explicit <c>scenarios</c>, a <c>where</c> condition that drops factor combinations,
    /// <c>derived</c> metrics computed per replication from the others and the factors, the <c>confidence</c> level, a <c>warmup</c>, and
    /// metrics split by a label value (<c>tally:x[*]:stat</c>). Applied to the study before it runs
    /// (<see cref="Apply"/>) and to its results after (<see cref="Finish"/>).
    /// </summary>
    public sealed class StudyOptions
    {
        public string Scenarios = "", Where = "", Derived = "";
        public double Confidence, Warmup = -1;
        /// <summary>The end condition "stop when no item has moved for T" (<see cref="Serialization.SimulationConfig.StopWhenNoProgressFor"/>):
        /// −1 = the model's, 0 = off.</summary>
        public double NoProgress = -1;

        /// <summary>The metrics with a <c>[*]</c> split, expanded after the runs: (name, path with the wildcard).</summary>
        public readonly List<(string name, string path)> Wildcards = new List<(string, string)>();
        private readonly List<(string name, Expression expr, string text)> derived = new List<(string, Expression, string)>();
        /// <summary>What the options accept but may not mean, filled by <see cref="Apply"/>: a derived metric that reads no
        /// metric and no factor (the same constant in every run). The commands add them to the answer's warnings.</summary>
        public readonly List<string> Warnings = new List<string>();

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
            if (NoProgress >= 0) study.Model.StopWhenNoProgressFor = NoProgress;
            // an empty list is no scenarios (scenarios=[] beside factors is accepted)
            string scenarios = (Scenarios ?? "").Trim();
            if (scenarios.Length > 0 && scenarios != "[]" && scenarios != "{}")
            {
                if (hasSet) throw new ArgumentException("scenarios: give the factors in 'factors' (a study file's 'set') or in the scenarios, not both.");
                AddScenarios(study);
            }
            if (!string.IsNullOrWhiteSpace(Where)) Filter(study);
            ParseDerived(study);
        }

        // [{"name": "A", "Buf1.capacity": 2, "replications": 5}, …] or {"A": {"Buf1.capacity": 2}, …}
        private void AddScenarios(Study study)
        {
            object json;
            try { json = MiniJson.Parse(Scenarios.Trim()); }
            catch (FormatException e)
            { throw new ArgumentException($"scenarios: not valid JSON ({e.Message}). Expected e.g. [{{\"name\": \"A\", \"Buffer.capacity\": 2}}].", e); }
            var list = new List<(string name, int reps, Dictionary<string, string> values)>();
            if (json is List<object> a)
                foreach (var item in a)
                {
                    if (!(item is Dictionary<string, object> o)) throw new ArgumentException("scenarios: each item is an object of factor paths and values.");
                    list.Add(Scenario(o.TryGetValue("name", out object n) && n is string name ? name : $"S{list.Count + 1}", o));
                }
            else if (json is Dictionary<string, object> byName)
                foreach (var kv in byName)
                {
                    if (!(kv.Value is Dictionary<string, object> o)) throw new ArgumentException($"scenarios: '{kv.Key}' is not an object of factor paths and values.");
                    list.Add(Scenario(kv.Key, o));
                }
            if (list.Count == 0) throw new ArgumentException("scenarios: none given.");
            var paths = new List<string>();
            foreach (var s in list) foreach (string p in s.values.Keys) if (!paths.Contains(p)) paths.Add(p);
            foreach (var s in list)
            {
                var missing = paths.Where(p => !s.values.ContainsKey(p)).ToList();
                if (missing.Count > 0) throw new ArgumentException($"scenarios: '{s.name}' gives no value for {Missing(study, missing)} — every scenario gives every factor any of them gives.");
            }
            foreach (string p in paths) study.Factor(p, list.Select(s => s.values[p]).Distinct().ToArray());
            foreach (var s in list) study.Scenario(s.name, s.reps, paths.Select(p => s.values[p]).ToArray());
        }

        // each missing factor with the model's value to copy: "B.capacity (the model's value: 4), B.mode (Fifo)"
        private static string Missing(Study study, List<string> paths)
        {
            var parts = new List<string>();
            bool said = false;
            foreach (string p in paths)
            {
                string v = study.CurrentLevel(p);
                if (v == null) { parts.Add(p); continue; }
                parts.Add($"{p} ({(said ? "" : "the model's value: ")}{v})");
                said = true;
            }
            return string.Join(", ", parts);
        }

        private static (string, int, Dictionary<string, string>) Scenario(string name, Dictionary<string, object> o)
        {
            var values = new Dictionary<string, string>();
            int reps = 0;
            foreach (var kv in o)
            {
                if (kv.Key == "name") continue;
                if (kv.Key == "replications") { reps = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture); continue; }
                values[kv.Key] = StudyInputs.LevelText(kv.Value, $"scenarios: '{name}': '{kv.Key}'");
            }
            return (name, reps, values);
        }

        // keeps the factor combinations for which the condition holds, as explicit scenarios
        private void Filter(Study study)
        {
            var labels = study.FactorLabels;
            if (labels.Count == 0) throw new ArgumentException("where: it filters the combinations of the factors ('factors', a study file's 'set'); give factors.");
            Expression condition;
            try { condition = Expression.ParseCondition(Where, ExpressionContext.Study); }
            catch (Exception e) { throw new ArgumentException($"where: {e.Message}", e); }
            var names = labels.Select(NameOf).ToList();
            var plan = study.Plan();
            var kept = new List<StudyScenario>();
            foreach (var s in plan)
            {
                var scope = new Scope();
                for (int i = 0; i < names.Count; i++) scope.Set(names[i], s.Values[i]);
                bool ok;
                try { ok = condition.Condition(scope); }
                catch (Exception e) { throw new ArgumentException($"where: {e.Message}", e); }
                if (scope.Missing.Count > 0)
                    throw new ArgumentException($"where: unknown name {string.Join(", ", scope.Missing.Distinct())} (the factors are {string.Join(", ", names)}).");
                if (ok) kept.Add(s);
            }
            if (kept.Count == 0) throw new ArgumentException($"where: no combination of the {plan.Count} meets '{Where}'.");
            foreach (var s in kept) study.Scenario(s.Name, s.Replications, s.Values.ToArray());
        }

        // a derived metric reads the metrics and the study's factors, named as in where (a study refuses a metric named as a factor; a derived one named so hides it)
        private void ParseDerived(Study study)
        {
            derived.Clear();
            Warnings.Clear();
            if (string.IsNullOrWhiteSpace(Derived)) return;
            var known = new HashSet<string>(study.Metrics.Select(m => m.Name));
            var factors = study.FactorLabels.Select(NameOf).Where(f => !known.Contains(f)).Distinct().ToList();
            foreach (var (name, text) in StudyInputs.MetricsOf(Derived))
            {
                if (known.Contains(name)) throw new ArgumentException($"derived: '{name}' is already a metric.");
                Expression e;
                try { e = Expression.Parse(text, ExpressionContext.Study); }
                catch (Exception ex) { throw new ArgumentException($"derived '{name}': {ex.Message}", ex); }
                var scope = new Scope();
                foreach (string f in factors) scope.Set(f, 1.0);
                foreach (string k in known) scope.Set(k, 1.0);
                bool evaluated = true;
                try { e.Number(scope); } catch (Exception) { evaluated = false; /* a value of 1 everywhere may divide by zero: only names are checked */ }
                if (scope.Missing.Count > 0)
                    throw new ArgumentException($"derived '{name}': unknown name {string.Join(", ", scope.Missing.Distinct())} (the metrics are " +
                                                $"{string.Join(", ", known)}; derived metrics may use those before them" +
                                                (factors.Count > 0 ? $"; the factors are {string.Join(", ", factors)}" : "; the study has no factors") +
                                                ", named as in where: B1.capacity → B1_capacity).");
                // a constant is a legitimate reference value, but more often a slip (stalls=0 meant a metric)
                if (evaluated && scope.Read.Count == 0)
                    Warnings.Add($"derived '{name}' reads no metric and no factor: it is the same constant ({text.Trim()}) in every run.");
                derived.Add((name, e, text));
                known.Add(name);
            }
        }

        /// <summary>After the runs: the wildcard metrics expanded per label value (<c>name[value]</c>), then the derived
        /// metrics of each completed run.</summary>
        public void Finish(ResultSet set)
        {
            foreach (var (name, path) in Wildcards) Expand(set, name, path);
            var levels = new Dictionary<string, List<KeyValuePair<string, string>>>();
            foreach (var sc in set.Scenarios) levels[sc.Name ?? ""] = sc.Parameters;
            foreach (var (name, expr, _) in derived)
            {
                set.MetricNames.Add(name);
                foreach (var run in set.Runs.Where(r => r.Error == null))
                {
                    var scope = new Scope();
                    // the scenario's factor levels first, so an earlier derived metric of the same name hides it
                    if (levels.TryGetValue(run.Scenario ?? "", out var parameters))
                        foreach (var kv in parameters) scope.Set(NameOf(kv.Key), kv.Value);
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
            var scope = MetricCatalog.ResultScopeOf(metric.Scope);
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
            /// <summary>Every name the expression asked for, known or not.</summary>
            public readonly List<string> Read = new List<string>();
            public double Now => 0;

            public void Set(string name, double v) => values[name] = ExprValue.Of(v);
            public void Set(string name, string text)
                => values[name] = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? ExprValue.Of(d) : ExprValue.Of(text);

            public ExprValue Get(string root, string name)
            {
                string key = root == null ? name : root + "." + name;
                Read.Add(key);
                if (values.TryGetValue(key, out var v)) return v;
                Missing.Add(key);
                return ExprValue.None;
            }
        }
    }
}
