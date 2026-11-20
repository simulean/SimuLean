using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using SimuLean.Serialization;

namespace SimuLean.Rules
{
    /// <summary>One parameter of a choice rule (a <see cref="RuleParameterAttribute"/> field).</summary>
    public sealed class RuleParameterInfo
    {
        /// <summary>The field's name: the record's name and the catalog key's last part (<c>rule.&lt;Name&gt;</c>).</summary>
        public string Name;
        /// <summary>Its label.</summary>
        public string Label;
        /// <summary>The field's type.</summary>
        public Type Type;
        /// <summary>The field's initial value (a new instance's).</summary>
        public object Default;
        /// <summary>The valid range (NaN = none).</summary>
        public double Min = double.NaN, Max = double.NaN;
        /// <summary>A number or a yes / no: a catalog parameter.</summary>
        public bool IsNumber => Type == typeof(bool) || Type == typeof(double) || Type == typeof(float) || Type == typeof(int) || Type == typeof(long);
        internal FieldInfo Field;
    }

    /// <summary>
    /// The registry of choice rules: every class implementing <see cref="IChoiceRule"/> in the loaded
    /// assemblies that reference the engine (Unity's <c>SimuLean.UserLogic</c>; the runner and the standalone tests compile the
    /// user-logic folder with the engine). Finds a rule by its type name, checks its site and parameters with a "did you mean",
    /// builds an instance with the owner's parameter records, scans the contract (no static mutable state, no
    /// <c>System.Random</c>) and gives a source hash. Reflection only at build and in the editor, never during a run.
    /// </summary>
    public static class ChoiceRules
    {
        private static List<Type> all;
        private static readonly object gate = new object();

        /// <summary>The settings records of an owner's rule parameters (<c>rule</c>; a parameter is <c>rule.&lt;Field&gt;</c>).</summary>
        public const string Key = "rule";

        /// <summary>The prefix of a team's request order that names a dispatch rule (<c>rule:HighestPriority</c>).</summary>
        public const string OrderPrefix = "rule:";

        /// <summary>Every rule type, by name.</summary>
        public static IReadOnlyList<Type> All
        {
            get
            {
                lock (gate)
                {
                    if (all != null) return all;
                    var list = new List<Type>();
                    var engine = typeof(IChoiceRule).Assembly;
                    string engineName = engine.GetName().Name;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.IsDynamic) continue;
                        if (a != engine)
                        {
                            bool refs;
                            try { refs = a.GetReferencedAssemblies().Any(n => n.Name == engineName); }
                            catch { refs = false; }
                            if (!refs) continue;
                        }
                        Type[] types;
                        try { types = a.GetTypes(); }
                        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                        foreach (var t in types)
                            if (t.IsClass && !t.IsAbstract && typeof(IChoiceRule).IsAssignableFrom(t)) list.Add(t);
                    }
                    list.Sort((x, y) => string.CompareOrdinal(x.FullName, y.FullName));
                    return all = list;
                }
            }
        }

        /// <summary>Forgets the registry (an assembly loaded since).</summary>
        public static void Refresh() { lock (gate) all = null; }

        /// <summary>The sites a rule type can decide (the interfaces it implements).</summary>
        public static List<RuleSite> SitesOf(Type t)
        {
            var s = new List<RuleSite>();
            if (t == null) return s;
            if (typeof(IRoutingRule).IsAssignableFrom(t)) s.Add(RuleSite.Routing);
            if (typeof(IReleaseRule).IsAssignableFrom(t)) s.Add(RuleSite.Release);
            if (typeof(IDispatchRule).IsAssignableFrom(t)) s.Add(RuleSite.Dispatch);
            return s;
        }

        /// <summary>The rule site of a decision site (Routing, Order = Release, Dispatch).</summary>
        public static RuleSite SiteOf(LogicSite logic)
            => logic == LogicSite.Routing ? RuleSite.Routing : logic == LogicSite.Order ? RuleSite.Release
             : logic == LogicSite.Dispatch ? RuleSite.Dispatch : throw new ArgumentException($"No choice rule decides a {logic} site.");

        /// <summary>The interface of a site.</summary>
        public static Type InterfaceOf(RuleSite site)
            => site == RuleSite.Routing ? typeof(IRoutingRule) : site == RuleSite.Release ? typeof(IReleaseRule) : typeof(IDispatchRule);

        /// <summary>The names of the rules of a site (their type names), sorted.</summary>
        public static string[] Names(RuleSite site)
            => All.Where(t => InterfaceOf(site).IsAssignableFrom(t)).Select(t => t.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();

        /// <summary>The rule type named <paramref name="name"/> (its type name, or its full name), or null.</summary>
        public static Type Find(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return null;
            var full = All.FirstOrDefault(t => t.FullName == name);
            if (full != null) return full;
            var byName = All.Where(t => t.Name == name).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        /// <summary>
        /// The rule type <paramref name="name"/> for <paramref name="site"/>: an error naming the owner when it is missing (with
        /// the closest names), ambiguous (two types of that name: give the full name), of another site, abstract or without a
        /// public parameterless constructor.
        /// </summary>
        public static Type Resolve(string name, RuleSite site, string owner)
        {
            name = (name ?? "").Trim();
            string what = RuleSiteBase.SettingsKey(site);
            if (name.Length == 0) throw new FormatException($"{owner}: a {what} rule needs the rule's type name.");
            var t = Find(name);
            if (t == null)
            {
                var same = All.Where(x => x.Name == name).ToList();
                if (same.Count > 1)
                    throw new FormatException($"{owner}: {same.Count} rules are named '{name}' ({string.Join(", ", same.Select(x => x.FullName))}): name it by its full name.");
                throw new FormatException($"{owner}: no choice rule '{name}' (a class implementing {InterfaceOf(site).Name} in SimuLean.UserLogic)." +
                                          NameSuggestions.DidYouMean(name, Names(site)));
            }
            if (!InterfaceOf(site).IsAssignableFrom(t))
            {
                var sites = SitesOf(t);
                throw new FormatException($"{owner}: '{t.Name}' is not a {what} rule (it implements {(sites.Count == 0 ? "no site's interface" : string.Join(", ", sites.Select(s => InterfaceOf(s).Name)))}, " +
                                          $"a {what} rule implements {InterfaceOf(site).Name})." + NameSuggestions.DidYouMean(name, Names(site)));
            }
            if (t.GetConstructor(Type.EmptyTypes) == null)
                throw new FormatException($"{owner}: rule '{t.Name}' needs a public constructor without arguments.");
            return t;
        }

        /// <summary>The parameters of a rule type (its <see cref="RuleParameterAttribute"/> fields), in declaration order.</summary>
        public static List<RuleParameterInfo> Parameters(Type t)
        {
            var list = new List<RuleParameterInfo>();
            if (t == null) return list;
            object fresh = null;
            try { fresh = Activator.CreateInstance(t); } catch { /* reported by Resolve / Contract */ }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var a = (RuleParameterAttribute)Attribute.GetCustomAttribute(f, typeof(RuleParameterAttribute));
                if (a == null) continue;
                list.Add(new RuleParameterInfo
                {
                    Name = f.Name, Label = a.Label ?? f.Name, Type = f.FieldType, Field = f, Min = a.Min, Max = a.Max,
                    Default = fresh == null ? null : f.GetValue(fresh),
                });
            }
            return list;
        }

        /// <summary>A rule's label, <c>Why</c> and declared reads (<see cref="ChoiceRuleAttribute"/>).</summary>
        public static ChoiceRuleAttribute Describe(Type t)
            => t == null ? null : (ChoiceRuleAttribute)Attribute.GetCustomAttribute(t, typeof(ChoiceRuleAttribute));

        /// <summary>
        /// What breaks the rule contract in a rule type, found by reflection: static fields that are not constants or read-only
        /// (state shared between runs and threads), fields of forbidden types (<c>System.Random</c>, threads, streams, files),
        /// <see cref="RuleParameterAttribute"/> fields that are static, of an unsupported type or not public. Empty = none found.
        /// (A scan of what the code calls is not made: the assembly has no Unity reference and the context offers only reads.)
        /// </summary>
        public static List<string> ContractProblems(Type t)
        {
            var problems = new List<string>();
            if (t == null) return problems;
            if (SitesOf(t).Count == 0) problems.Add($"{t.Name} implements no site's interface (IRoutingRule, IReleaseRule, IDispatchRule)");
            if (SitesOf(t).Count > 1) problems.Add($"{t.Name} implements several sites' interfaces: one rule, one decision");
            if (t.GetConstructor(Type.EmptyTypes) == null) problems.Add($"{t.Name} has no public constructor without arguments");
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                foreach (var f in x.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral) continue;
                    if (f.IsStatic && !f.IsInitOnly) problems.Add($"static field '{f.Name}': a rule keeps no static state (its instance fields are its memory within a run)");
                    if (Forbidden(f.FieldType)) problems.Add($"field '{f.Name}' is a {f.FieldType.Name}: draw from context.Random, no threads or I/O");
                    var a = Attribute.GetCustomAttribute(f, typeof(RuleParameterAttribute));
                    if (a != null && (f.IsStatic || !f.IsPublic)) problems.Add($"parameter '{f.Name}' must be a public instance field");
                    if (a != null && !Supported(f.FieldType)) problems.Add($"parameter '{f.Name}' is a {f.FieldType.Name} (a number, whole number, yes / no, text or option)");
                }
            return problems;
        }

        private static bool Forbidden(Type t)
        {
            string n = t.FullName ?? "";
            return t == typeof(Random) || n.StartsWith("System.Threading.Thread", StringComparison.Ordinal) || n.StartsWith("System.Threading.Tasks", StringComparison.Ordinal)
                   || typeof(Stream).IsAssignableFrom(t) || typeof(TextReader).IsAssignableFrom(t) || typeof(TextWriter).IsAssignableFrom(t)
                   || n.StartsWith("UnityEngine.", StringComparison.Ordinal);
        }

        private static bool Supported(Type t)
            => t == typeof(double) || t == typeof(float) || t == typeof(int) || t == typeof(long) || t == typeof(bool) || t == typeof(string) || t.IsEnum;

        /// <summary>
        /// A new instance of the rule <paramref name="name"/> for <paramref name="site"/>, its parameters set from
        /// <paramref name="records"/> (the owner's <c>rule</c> records; a missing one keeps the field's initial value). A record
        /// that is no parameter of any of <paramref name="ownerRules"/> (the rules the owner uses), a value of the wrong kind or out
        /// of range, and a contract problem fail the build naming the owner.
        /// </summary>
        public static IChoiceRule Create(string name, RuleSite site, IEnumerable<PropertySettings> records, IEnumerable<string> ownerRules, string owner)
        {
            var t = Resolve(name, site, owner);
            var problems = ContractProblems(t);
            if (problems.Count > 0) throw new FormatException($"{owner}: rule '{t.Name}' breaks the rule contract: {string.Join("; ", problems)}.");
            var rule = (IChoiceRule)Activator.CreateInstance(t);
            var parameters = Parameters(t);
            var known = new List<string>();
            foreach (string r in ownerRules ?? new[] { name })
            {
                var rt = Find(r);
                if (rt != null) known.AddRange(Parameters(rt).Select(p => p.Name));
            }
            foreach (var rec in records ?? Enumerable.Empty<PropertySettings>())
            {
                if (rec == null) continue;
                string n = (rec.Name ?? "").Trim();
                if (n.Length == 0) throw new FormatException($"{owner}: a rule parameter without a name.");
                var p = parameters.FirstOrDefault(x => x.Name == n);
                if (p == null)
                {
                    if (known.Contains(n)) continue;   // another rule's of the same owner
                    throw new FormatException($"{owner}: rule '{t.Name}' has no parameter '{n}'" +
                                              (parameters.Count == 0 ? " (it has none)." : $" ({string.Join(", ", parameters.Select(x => x.Name))}).") +
                                              NameSuggestions.DidYouMean(n, parameters.Select(x => x.Name)));
                }
                p.Field.SetValue(rule, Value(p, rec, owner, t.Name));
            }
            return rule;
        }

        private static object Value(RuleParameterInfo p, PropertySettings rec, string owner, string rule)
        {
            string where = $"{owner}: rule '{rule}' parameter '{p.Name}'";
            Type t = p.Type;
            if (t == typeof(string)) return rec.Kind == PropertyValueKind.Number ? rec.Value.ToString("R", CultureInfo.InvariantCulture) : rec.Text ?? "";
            if (t.IsEnum)
            {
                string text = (rec.Text ?? "").Trim();
                foreach (string n in Enum.GetNames(t)) if (string.Equals(n, text, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(t, n);
                throw new FormatException($"{where}: '{text}' is not one of {string.Join(", ", Enum.GetNames(t))}.");
            }
            double v;
            if (rec.Kind == PropertyValueKind.Number) v = rec.Value;
            else if (t == typeof(bool) && bool.TryParse((rec.Text ?? "").Trim(), out bool b)) v = b ? 1 : 0;
            else if (!double.TryParse((rec.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new FormatException($"{where} is a number, not '{rec.Text}'.");
            if (double.IsNaN(v) || double.IsInfinity(v)) throw new FormatException($"{where} is not a finite number.");
            if (!double.IsNaN(p.Min) && v < p.Min) throw new FormatException($"{where} = {v.ToString("R", CultureInfo.InvariantCulture)} is below its minimum {p.Min.ToString("R", CultureInfo.InvariantCulture)}.");
            if (!double.IsNaN(p.Max) && v > p.Max) throw new FormatException($"{where} = {v.ToString("R", CultureInfo.InvariantCulture)} is above its maximum {p.Max.ToString("R", CultureInfo.InvariantCulture)}.");
            if (t == typeof(bool))
            {
                if (v != 0 && v != 1) throw new FormatException($"{where} is a yes / no (0 or 1), not {v.ToString("R", CultureInfo.InvariantCulture)}.");
                return v == 1;
            }
            if (t == typeof(int) || t == typeof(long))
            {
                if (Math.Floor(v) != v) throw new FormatException($"{where} is a whole number, not {v.ToString("R", CultureInfo.InvariantCulture)}.");
                return t == typeof(int) ? (object)(int)v : (long)v;
            }
            return t == typeof(float) ? (object)(float)v : v;
        }

        /// <summary>The record of a parameter at its value (a number or yes / no as a number, a text or option as a text).</summary>
        public static PropertySettings RecordOf(RuleParameterInfo p, object value)
        {
            if (p.Type == typeof(string)) return PropertySettings.OfText(p.Name, value as string ?? "");
            if (p.Type.IsEnum) return PropertySettings.OfText(p.Name, value?.ToString() ?? "");
            if (value is bool b) return PropertySettings.Number(p.Name, b ? 1 : 0);
            return PropertySettings.Number(p.Name, Convert.ToDouble(value ?? 0.0, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// <paramref name="records"/> with a record added for every parameter of <paramref name="rules"/> it does not have (at the
        /// field's initial value), so the catalog lists every parameter of the rules the owner uses. The same list (created when
        /// null and something is added).
        /// </summary>
        public static List<PropertySettings> Complete(List<PropertySettings> records, IEnumerable<string> rules)
        {
            foreach (string r in rules ?? Enumerable.Empty<string>())
                foreach (var p in Parameters(Find(r)))
                {
                    if (records != null && records.Any(x => x != null && (x.Name ?? "").Trim() == p.Name)) continue;
                    (records ?? (records = new List<PropertySettings>())).Add(RecordOf(p, p.Default));
                }
            return records;
        }

        /// <summary>The rules an element's settings name (its routing rule, its release rule).</summary>
        public static List<string> RulesOf(ElementSettings s)
        {
            var list = new List<string>();
            if (s == null) return list;
            var o = s.OutputStrategy;
            if (o != null && BuilderChoices.Routing(o.StrategyType) == HeadlessOutputStrategyType.Rule && !string.IsNullOrWhiteSpace(o.Rule))
                list.Add(o.Rule.Trim());
            if (s is QueueSettings q && !string.IsNullOrWhiteSpace(q.OfferRule)) list.Add(q.OfferRule.Trim());
            return list;
        }

        /// <summary>The dispatch rule a team's request order names (<c>rule:X</c> written in place or as a named order's query), or null.</summary>
        public static string RuleOf(ResourceTeamConfig t)
        {
            if (t == null) return null;
            string text;
            try { text = ResourceBuild.NamedOrder(t); }
            catch (ArgumentException) { return null; }
            return OrderRule(text);
        }

        /// <summary>The rule an order text names (<c>rule:X</c> → X), else null.</summary>
        public static string OrderRule(string text)
        {
            text = (text ?? "").Trim();
            return text.StartsWith(OrderPrefix, StringComparison.Ordinal) ? text.Substring(OrderPrefix.Length).Trim() : null;
        }

        // ── sources ──────────────────────────────────────────────────────

        /// <summary>The folder of the rule sources (null = <c>Assets/SimuLean.UserLogic</c> under the current directory, when there).</summary>
        public static string SourceFolder { get; set; }

        private static string Folder()
        {
            string f = SourceFolder ?? Path.Combine(Directory.GetCurrentDirectory(), "Assets", "SimuLean.UserLogic");
            return Directory.Exists(f) ? f : null;
        }

        /// <summary>The source file of a rule (<c>&lt;Type&gt;.cs</c> under the source folder, else a file that declares it), or null.</summary>
        public static string SourceFile(Type t)
        {
            string folder = t == null ? null : Folder();
            if (folder == null) return null;
            var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);
            var named = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == t.Name);
            if (named != null) return named;
            var decl = new System.Text.RegularExpressions.Regex(@"\b(class|struct)\s+" + System.Text.RegularExpressions.Regex.Escape(t.Name) + @"\b");
            return files.FirstOrDefault(f => decl.IsMatch(File.ReadAllText(f)));
        }

        /// <summary>The first 8 hexadecimal digits of the SHA-256 of a rule's source (carriage returns removed), or null when
        /// the source is not found (a runner without sources).</summary>
        public static string SourceHash(Type t)
        {
            string f = SourceFile(t);
            if (f == null) return null;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(f).Replace("\r", "")));
                return string.Concat(h.Take(4).Select(b => b.ToString("x2")));
            }
        }

        /// <summary><c>rule: Type (source 1a2b3c4d)</c>, as the logic viewer shows a rule.</summary>
        public static string Display(string name)
        {
            var t = Find(name);
            string hash = t == null ? null : SourceHash(t);
            return "rule: " + (t?.Name ?? name) + (t == null ? " (not found)" : hash != null ? $" (source {hash})" : "");
        }
    }
}
