// Architecture lint: the conventions of CLAUDE.md that only the documentation
// enforced become gate failures — text rules over the C# sources, each with an allow-list of the files where the
// pattern is the rule's own implementation or not model code (allowlist.txt: "rule path # reason"). Existing
// exceptions are listed, not fixed (no change of engine semantics); a new use outside the list fails.
// Run: dotnet run  (from StandaloneTests/architecturelint)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

static class Program
{
    sealed class Rule
    {
        public string Id, What, Why;
        public Regex Pattern;
        public Func<string, bool> Applies = _ => true;   // by repository-relative path
    }

    static readonly Rule[] Rules =
    {
        new Rule
        {
            Id = "schedule-work", What = "SimClock.ScheduleEvent(", Pattern = new Regex(@"\.ScheduleEvent\s*\("),
            Why = "timed work of an element goes through Element.ScheduleWork, so downtime (Element.Stop) can pause it",
        },
        new Rule
        {
            Id = "random-streams", What = "a global / unseeded random source",
            Pattern = new Regex(@"RandomEngineManager\.GlobalEngine|new\s+(System\.)?Random\s*\(|UnityEngine\.Random\b|(?<![\w.])Random\.(Range|value|insideUnitSphere|insideUnitCircle|onUnitSphere|rotation|InitState)\b"),
            Why = "every sampler draws from its element's stream (clock.Context.AssignRandomStream / CreateRandomStream)",
        },
        new Rule
        {
            Id = "unity-compat", What = "a direct Unity object lookup / identity call",
            Pattern = new Regex(@"\b(FindObjectOfType|FindObjectsOfType|FindFirstObjectByType|FindObjectsByType|FindAnyObjectByType|GetInstanceID|GetEntityId)\b"),
            Why = "UnityCompat (FindAny / FindAll / ObjectId) is the single place that adapts to Unity's API changes",
            Applies = p => p.StartsWith("Assets/SimuLean.Unity/") || p.StartsWith("Assets/SimuLean.Optimization/") || p.StartsWith("Assets/SimuLean.Experimentation/"),
        },
        new Rule
        {
            Id = "no-runtime-setup", What = "[RuntimeInitializeOnLoadMethod]", Pattern = new Regex(@"RuntimeInitializeOnLoadMethod"),
            Why = "nothing is added at run time: the editor-only scene set-up adds the optional components",
        },
        new Rule
        {
            Id = "engine-no-unity", What = "a Unity namespace in the engine", Pattern = new Regex(@"^\s*using\s+(UnityEngine|UnityEditor)\b", RegexOptions.Multiline),
            Why = "the engine (SimuLean.Net) has no Unity reference: headless runs and the standalone tests build it alone",
            Applies = p => p.StartsWith("Assets/SimuLean.Net/"),
        },
    };

    // A comment that dates itself or cites the round that wrote it: the history is in git. Architecture "§" pointers and
    // "roadmap §11.x" decision citations are intent and stay allowed.
    const string HistoryRuleId = "comment-history", HistoryWhat = "a date / review / phase / tracker tag in a comment",
                 HistoryWhy = "comments say why, not when: the history lives in git";
    static readonly Regex HistoryTag = new Regex(
        @"\b202\d-\d\d-\d\d\b(?!\s+\d\d:\d\d)" +                 // a date (not a calendar instant: a date with its time)
        @"|\b[Rr]eview\s*(#|\d|[A-Z]\d|round\b|block\b)" +       // review #3, review 2026-…, review B4, review round
        @"|\b[Pp]hase\s+(\d|[A-Z]{1,2}\d*\b)" +
        @"|\b[Bb]attery\s+(B?\d|round\b)" +
        @"|\b[Rr]ound\s+\d" +                                    // round 2
        @"|\b\d\.\d-b\d+" +                                      // 6.0-b3
        @"|\bpaper-v\d|\bP-[A-Z]\b|\bCS\d\b" +                   // tracker names
        @"|\bCASE\s?\d+\b|\b[Cc]ase[- ]study\s*#?\d" +           // a case-study tag: CASE2, case-study 3
        @"|\bv\d+\s+run\b|\b[Rr]un\s+v\d+\b" +                   // a numbered run: v2 run, run v3
        @"|\((?:[A-Z]{1,2}\d{1,2}[a-z]?|R-\d+[a-z]?)[;,:)]" +    // a tracker id in brackets: (P1), (S4): (R-11)
        @"|\bas before\b");
    static bool HistoryApplies(string path) =>
        path.StartsWith("Assets/SimuLean.Net/") || path.StartsWith("Assets/SimuLean.Unity/")
        || path.StartsWith("Assets/SimuLean.Optimization/") || path.StartsWith("Assets/SimuLean.Experimentation/");

    // a test class or test method named after the round that wrote it (BatteryB03CommandsTests, …Phase6Site): a test is
    // named after the behaviour it checks
    static readonly Regex HistoryName = new Regex(
        @"\b(?:class|void)\s+(?<name>\w*(?:Battery[A-Z]?\d|Round\d|Review(?:Round|[A-Z]?\d)|Phase\d|Paper[Vv]\d)\w*)");

    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static int Main()
    {
        string root = FindRoot();
        var allow = Allowed(Path.Combine(root, "StandaloneTests", "architecturelint", "allowlist.txt"));
        var sources = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (path: Path.GetRelativePath(root, f).Replace('\\', '/'), text: Code(File.ReadAllText(f)), raw: File.ReadAllText(f)))
            .ToList();
        var used = new HashSet<string>();

        // the rules themselves: each catches a sample violation, and not its mention in a comment or a string
        var samples = new Dictionary<string, string>
        {
            ["schedule-work"] = "clock.ScheduleEvent(job, delay);", ["random-streams"] = "var r = new System.Random();",
            ["unity-compat"] = "var c = Object.FindAnyObjectByType<Camera>();", ["no-runtime-setup"] = "[RuntimeInitializeOnLoadMethod] static void A() { }",
            ["engine-no-unity"] = "using UnityEngine;",
        };
        foreach (var rule in Rules)
        {
            Check(rule.Pattern.IsMatch(Code(samples[rule.Id])), $"self-test: '{rule.Id}' catches '{samples[rule.Id]}'");
            Check(!rule.Pattern.IsMatch(Code("// " + samples[rule.Id] + "\nstring s = \"" + samples[rule.Id].Replace("\"", "") + "\";")),
                  $"self-test: '{rule.Id}' ignores comments and strings");
        }

        foreach (var rule in Rules)
        {
            Console.WriteLine($"- {rule.Id}: {rule.What} only where allow-listed");
            foreach (var (path, text, _) in sources.Where(s => rule.Applies(s.path)))
            {
                if (!rule.Pattern.IsMatch(text)) continue;
                string key = rule.Id + " " + path;
                if (allow.Contains(key)) { used.Add(key); passed++; continue; }
                Check(false, $"{path}: {rule.What} — {rule.Why}. Use the rule, or add '{key} # reason' to allowlist.txt.");
            }
        }

        // comments say why, not when (CLAUDE.md): no dates, review / battery / round ids, phase names or tracker ids
        Console.WriteLine($"- {HistoryRuleId}: {HistoryWhat} only where allow-listed");
        Check(HistoryTag.IsMatch(Comments("int a = 1; // fixed in review #3 (2026-10-07)")), $"self-test: '{HistoryRuleId}' catches a dated review id");
        Check(HistoryTag.IsMatch(Comments("int a = 1; // (CASE2 v2 run: the agent needed the generic tools)")) && HistoryTag.IsMatch(Comments("// seen in run v3"))
              && !HistoryTag.IsMatch(Comments("switch (k) { case 2: break; } // case 2 is the empty lane; v2 of the format")),
              $"self-test: '{HistoryRuleId}' catches a case-study run tag, not a switch case or a version");
        Check(!HistoryTag.IsMatch(Comments("string s = \"Phase 4 2026-10-07\"; // see architecture §3.5, roadmap §11.2")),
              $"self-test: '{HistoryRuleId}' ignores strings and § citations");
        foreach (var (path, _, raw) in sources.Where(s => HistoryApplies(s.path)))
        {
            Match m = HistoryTag.Match(Comments(raw));
            if (!m.Success) continue;
            string key = HistoryRuleId + " " + path;
            if (allow.Contains(key)) { used.Add(key); passed++; continue; }
            Check(false, $"{path}: '{m.Value}' in a comment — {HistoryWhy}. Keep the rationale, drop the tag (or add '{key} # reason' to allowlist.txt).");
        }

        Console.WriteLine($"- {HistoryRuleId}: no test class or method named after a review / battery / round / phase");
        Check(HistoryName.IsMatch(Code("public class BatteryB03CommandsTests { }")) && HistoryName.IsMatch(Code("public void TheLogicShowsEachPhase6Site() { }"))
              && HistoryName.IsMatch(Code("class RebuildRound2CommandsTests { }")), $"self-test: '{HistoryRuleId}' catches a test named after a round");
        Check(!HistoryName.IsMatch(Code("public void TheFollowUpLoopRepeatsReviews() { } // class BatteryB03Tests")),
              $"self-test: '{HistoryRuleId}' ignores ordinary words and comments");
        foreach (var (path, text, _) in sources.Where(s => HistoryApplies(s.path)))
        {
            Match m = HistoryName.Match(text);
            if (!m.Success) continue;
            string key = HistoryRuleId + " " + path;
            if (allow.Contains(key)) { used.Add(key); passed++; continue; }
            Check(false, $"{path}: '{m.Groups["name"].Value}' is named after its history — {HistoryWhy}. Name it after the behaviour it checks.");
        }

        // the engine's namespaces depend on each other only in the directions listed (namespaces.txt): a new direction
        // fails, so a dependency cycle cannot grow unnoticed (architecture §2)
        Console.WriteLine("- namespace-deps: the engine's namespaces depend on each other only in the listed directions");
        var edges = Allowed(Path.Combine(root, "StandaloneTests", "architecturelint", "namespaces.txt"));
        var engineNamespaces = new HashSet<string>(sources.Where(s => s.path.StartsWith("Assets/SimuLean.Net/"))
            .Select(s => Regex.Match(s.text, @"\bnamespace\s+([\w.]+)")).Where(m => m.Success).Select(m => Top(m.Groups[1].Value)));
        var edgesUsed = new HashSet<string>();
        var sampleNamespaces = new HashSet<string> { "SimuLean", "SimuLean.Serialization", "SimuLean.Optimization", "SimuLean.Results", "SimuLean.Resources" };
        Check(NamespaceEdges("namespace SimuLean.Serialization { using SimuLean.Optimization; class A { SimuLean.Results.X x; SimuLean.SimContext c; } }", sampleNamespaces)
                  .SetEquals(new[] { "SimuLean.Serialization -> SimuLean.Optimization", "SimuLean.Serialization -> SimuLean.Results" }),
              "self-test: 'namespace-deps' finds a using and a qualified name (not a type of the root namespace)");
        Check(NamespaceEdges("namespace SimuLean.Resources { using SimuLean; using SimuLean.Resources.Inner; class A { } }", sampleNamespaces).Count == 0,
              "self-test: 'namespace-deps' ignores the parent namespace and its own");
        foreach (var (path, text, _) in sources.Where(s => s.path.StartsWith("Assets/SimuLean.Net/")))
            foreach (string edge in NamespaceEdges(text, engineNamespaces))
            {
                if (edges.Contains(edge)) { edgesUsed.Add(edge); passed++; continue; }
                Check(false, $"{path}: a new namespace dependency '{edge}' — the engine's namespaces depend on each other only in the " +
                             "directions of namespaces.txt (a reverse edge makes a cycle). Invert it (an interface or a parameter of the " +
                             "lower namespace, or move the code), or add the edge to namespaces.txt with a reason.");
            }
        foreach (string edge in edges.Where(e => !edgesUsed.Contains(e))) Console.WriteLine($"  note: namespace edge '{edge}' is no longer used");

        // every model element of the Library has an extractor (Unity) and a handler (engine), registered (architecture §3.5)
        Console.WriteLine("- element-registries: every [SimuLeanElement] SElement has a registered extractor and handler");
        string extractors = sources.First(s => s.path.EndsWith("Serialization/IElementExtractor.cs")).raw;     // aliases are strings
        string handlers = sources.First(s => s.path.EndsWith("Serialization/ElementHandlerRegistry.cs")).raw;
        var elementClass = new Regex(@"\[(SimuLean\.Unity\.)?SimuLeanElement\([^\]]*\)\]\s*(\[[^\]]*\]\s*)*public\s+(sealed\s+)?class\s+(?<name>\w+)\s*:\s*(?<base>[\w.]+)");
        int elements = 0;
        foreach (var (path, text, _) in sources)
            foreach (Match m in elementClass.Matches(text))
            {
                string name = m.Groups["name"].Value;
                elements++;
                string key = "element-registries " + name;
                // the extractor is named after the wrapper (UnityQueueExtractor), the handler after the engine type (QueueHandler)
                bool ok = Registered(extractors, name, "Extractor")
                          && (extractors.Contains("RegisterAlias(\"" + name + "\"")   // a wrapper subclass: its extractor's engine type
                              || Registered(handlers, name.StartsWith("Unity", StringComparison.Ordinal) ? name.Substring(5) : name, "Handler"));
                if (ok) { passed++; continue; }
                if (allow.Contains(key)) { used.Add(key); passed++; continue; }
                Check(false, $"{path}: '{name}' is a Library element without a registered extractor / handler — a scene run and a " +
                             $"headless run could not build it (register both, or add '{key} # reason').");
            }
        Check(elements > 10, $"element classes found ({elements})");

        foreach (string key in allow.Where(k => !used.Contains(k))) Console.WriteLine($"  note: allow-list entry '{key}' is no longer needed");
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static bool Registered(string registry, string type, string suffix)
        => Regex.IsMatch(registry, @"new\s+" + Regex.Escape(type) + suffix + @"\s*\(") || registry.Contains("RegisterAlias(\"" + type + "\"");

    /// <summary>The dependencies of a source (comments and strings removed) on the other namespaces of the engine, as
    /// "SimuLean.A -> SimuLean.B" between top namespaces (<c>SimuLean.X</c>, the root <c>SimuLean</c> included): its
    /// <c>using</c> directives and the names it qualifies with <c>SimuLean.</c> (a name qualified from a parent namespace
    /// without the prefix is not seen; a qualified type of the root namespace is not one: only <paramref name="namespaces"/>,
    /// the top namespaces the engine declares, count). The root and a namespace's own children are not dependencies.</summary>
    static HashSet<string> NamespaceEdges(string code, HashSet<string> namespaces)
    {
        var edges = new HashSet<string>(StringComparer.Ordinal);
        var ns = Regex.Match(code, @"\bnamespace\s+([\w.]+)");
        if (!ns.Success) return edges;
        string own = Top(ns.Groups[1].Value);
        foreach (Match m in Regex.Matches(code, @"\busing\s+(?:static\s+)?(?:\w+\s*=\s*)?(SimuLean(?:\.\w+)*)|(?<![\w.])(SimuLean(?:\.[A-Z]\w*)+)"))
        {
            string target = Top(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            if (target != own && target != "SimuLean" && namespaces.Contains(target)) edges.Add(own + " -> " + target);
        }
        return edges;
    }

    static string Top(string ns) { var p = ns.Split('.'); return p.Length >= 2 ? p[0] + "." + p[1] : p[0]; }

    /// <summary>The comments of a source, one per line (string and char literals skipped, so "//" in a string is not one).</summary>
    static string Comments(string s)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                int e = s.IndexOf('\n', i); if (e < 0) e = n;
                sb.Append(s, i + 2, e - i - 2).Append('\n'); i = e;
            }
            else if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); if (e < 0) e = n - 2;
                sb.Append(s, i + 2, e - i - 2).Append('\n'); i = e + 2;
            }
            else if (c == '"' || ((c == '@' || c == '$') && i + 1 < n && (s[i + 1] == '"' || s[i + 1] == '@' || s[i + 1] == '$')))
            {
                bool verbatim = false;
                while (s[i] == '@' || s[i] == '$') { verbatim |= s[i] == '@'; i++; }
                i++;
                while (i < n)
                {
                    if (!verbatim && s[i] == '\\') { i += 2; continue; }
                    if (s[i] == '"') { if (verbatim && i + 1 < n && s[i + 1] == '"') { i += 2; continue; } i++; break; }
                    i++;
                }
            }
            else if (c == '\'')
            {
                i++;
                while (i < n && s[i] != '\'') i += s[i] == '\\' ? 2 : 1;
                i++;
            }
            else i++;
        }
        return sb.ToString();
    }

    /// <summary>The source without comments and string literals (a rule names code, not prose).</summary>
    static string Code(string s)
    {
        s = Regex.Replace(s, @"/\*.*?\*/", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"//[^\n]*", "");
        s = Regex.Replace(s, @"@""(?:[^""]|"""")*""", "\"\"");
        s = Regex.Replace(s, @"""(?:\\.|[^""\\\n])*""", "\"\"");
        return s;
    }

    static HashSet<string> Allowed(string file)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(file)) return set;
        foreach (string line in File.ReadAllLines(file))
        {
            string l = line.Trim();
            if (l.Length == 0 || l.StartsWith("#")) continue;
            int hash = l.IndexOf(" #", StringComparison.Ordinal);
            set.Add(Regex.Replace((hash >= 0 ? l.Substring(0, hash) : l).Trim(), @"\s+", " "));
        }
        return set;
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "CLAUDE.md"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root (CLAUDE.md) not found.");
    }
}
