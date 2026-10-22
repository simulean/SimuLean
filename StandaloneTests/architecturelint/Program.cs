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
                bool ok = Registered(extractors, name, "Extractor") && Registered(handlers, name, "Handler");
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
