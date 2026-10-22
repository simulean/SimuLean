// Doc-symbol check: every back-ticked code name in the agent-facing documentation
// (CLAUDE.md, docs/architecture.md, docs/project-overview.md) must exist in the code — a type or member name, a
// snake_case statistic or command name, a file path. Stale documentation is followed by agents as if it were true (a
// review found removed classes and renamed statistics cited as current). A name quoted as history (on a line
// that says it was removed / replaced / renamed…) is skipped; the rest of the exceptions are in allowlist.txt, each
// with its reason.
// Run: dotnet run  (from StandaloneTests/docsymboltests)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

static class Program
{
    static readonly string[] Docs = { "CLAUDE.md", "docs/architecture.md", "docs/project-overview.md" };

    // a line that quotes a name as history: the name is expected to be gone
    static readonly Regex History = new Regex(@"\b(removed|remove[sd]?|replaced|replaces|renamed|former(ly)?|dropped|deleted|no longer|gone|was\b|were\b|until\b|instead of|retired|obsolete|legacy|old\b|previous(ly)?|deprecated|migrat\w*|merged into|now\b)", RegexOptions.IgnoreCase);
    static readonly Regex Tick = new Regex("`([^`\r\n]+)`");
    // PascalCase names, dotted (Type.Member) or with a call / generic suffix
    static readonly Regex Pascal = new Regex(@"^(?<parts>[A-Z][A-Za-z0-9]*(\.[A-Za-z_][A-Za-z0-9_]*)*)(\(.*\)|<.*>)?$");
    // snake_case words: statistic names, MCP commands (simulean_run_headless), keys
    static readonly Regex Snake = new Regex(@"^[a-z][a-z0-9]*(_[a-z0-9]+)+$");
    static readonly Regex PathLike = new Regex(@"^[A-Za-z0-9_.\-/\\]+\.(cs|md|ps1|asmdef|unity|prefab|json|asset|py|csproj)$");

    static int Main(string[] args)
    {
        string root = FindRoot();
        var code = Corpus(root);
        var words = new HashSet<string>(Regex.Matches(code, @"[A-Za-z_][A-Za-z0-9_]*").Cast<Match>().Select(m => m.Value), StringComparer.Ordinal);
        var allow = Allowed(Path.Combine(root, "StandaloneTests", "docsymboltests", "allowlist.txt"));
        var files = new HashSet<string>(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Library" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        foreach (string f in files) words.Add(Path.GetFileNameWithoutExtension(f));   // prefabs, models, scenes by name
        int checkedNames = 0;
        var problems = new List<string>();
        // the check itself: it finds the stale names a review found by hand, and accepts current ones
        foreach (string stale in new[] { "RandomProcessConfig.CreateRandomProcess", "content_avg_tw", "Assets/SimuLean.Net/NoSuchFile.cs" })
            if (Missing(stale, words, files) == null) problems.Add($"self-test: `{stale}` was not reported");
        foreach (string current in new[] { "Element.ScheduleWork", "content_avg", "simulean_run_headless", "Assets/SimuLean.Net/SimClock/SimLog.cs" })
            if (Missing(current, words, files) != null) problems.Add($"self-test: `{current}` reported: {Missing(current, words, files)}");
        foreach (string line in new[] { "The former `X` was removed.", "Use `X` now." })
            if (!History.IsMatch(line) && line.Contains("former")) problems.Add("self-test: history not recognised: " + line);
        var usedAllow = new HashSet<string>();
        foreach (string doc in Docs)
        {
            var lines = File.ReadAllLines(Path.Combine(root, doc));
            foreach (var (line, sentence) in Sentences(lines))
            {
                if (History.IsMatch(sentence)) continue;
                foreach (Match m in Tick.Matches(sentence))
                {
                    string t = m.Groups[1].Value.Trim();
                    string missing = Missing(t, words, files);
                    if (missing == null) { if (Classified(t)) checkedNames++; continue; }
                    checkedNames++;
                    if (allow.Contains(t)) { usedAllow.Add(t); continue; }
                    problems.Add($"{doc}:{line}: `{t}` — {missing}");
                }
            }
        }
        foreach (var p in problems) Console.WriteLine("  FAIL: " + p);
        foreach (var a in allow.Where(a => !usedAllow.Contains(a))) Console.WriteLine($"  note: allow-list entry `{a}` is no longer needed");
        Console.WriteLine($"- doc symbols: {checkedNames} code names checked in {Docs.Length} documents");
        Console.WriteLine($"\n{checkedNames - problems.Count} passed, {problems.Count} failed");
        return problems.Count == 0 ? 0 : 1;
    }

    /// <summary>The sentences of a document with the line each starts on: a paragraph's lines joined (a table row, a list
    /// item and a heading are paragraphs of their own), split after '.', ';' or ':' — so a name quoted as history on the
    /// line after "removed" is still seen as history.</summary>
    static IEnumerable<(int line, string sentence)> Sentences(string[] lines)
    {
        var para = new System.Text.StringBuilder();
        int start = 0;
        var result = new List<(int, string)>();
        void Flush()
        {
            if (para.Length == 0) return;
            foreach (string part in Regex.Split(para.ToString(), @"(?<=[.;:])\s+(?=[A-Z(`*])"))
                result.Add((start, part));
            para.Clear();
        }
        bool fence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.TrimStart().StartsWith("```")) { Flush(); fence = !fence; continue; }
            bool newBlock = l.Trim().Length == 0 || l.TrimStart().StartsWith("|") || l.TrimStart().StartsWith("#")
                            || Regex.IsMatch(l, @"^\s*([-*]|\d+\.)\s");
            if (fence || newBlock) Flush();
            if (l.Trim().Length == 0) continue;
            if (para.Length == 0) start = i + 1;
            para.Append(' ').Append(l.Trim());
            if (fence || l.TrimStart().StartsWith("|") || l.TrimStart().StartsWith("#")) Flush();
        }
        Flush();
        return result;
    }

    static bool Classified(string t) => Pascal.IsMatch(t) || Snake.IsMatch(t) || PathLike.IsMatch(t);

    /// <summary>Why <paramref name="t"/> is not in the code (null: present, or not a code name).</summary>
    static string Missing(string t, HashSet<string> words, HashSet<string> files)
    {
        if (PathLike.IsMatch(t))
        {
            string p = t.Replace('\\', '/').TrimStart('/');
            if (files.Contains(p) || files.Any(f => f.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase))) return null;
            return "no such file";
        }
        if (Snake.IsMatch(t)) return words.Contains(t) ? null : "not in the code";
        var m = Pascal.Match(t);
        if (!m.Success) return null;
        var parts = m.Groups["parts"].Value.Split('.');
        // a file-like or namespace-like dotted name (SimuLean.Net, Assets...) is not checked part by part
        if (parts[0] == "SimuLean" || parts[0] == "UnityEngine" || parts[0] == "UnityEditor" || parts[0] == "System") return null;
        if (parts.Length == 1 && parts[0].Length < 4) return null;           // acronyms, units (KPI, CSV, MTBF…)
        if (parts.Length == 1 && parts[0].All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_')) return null;   // CONSTANT-like state names
        var absent = parts.Where(p => !words.Contains(p)).ToList();
        return absent.Count == 0 ? null : "not in the code: " + string.Join(", ", absent);
    }

    static string Corpus(string root)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string dir in new[] { "Assets", "StandaloneTests", "Tools", ".claude/hooks", ".githooks" })
        {
            string d = Path.Combine(root, dir);
            if (!Directory.Exists(d)) continue;
            foreach (string f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".cs" && ext != ".ps1" && ext != ".py" && ext != ".asmdef" && ext != ".json" && ext != ".sh" && Path.GetFileName(f) != "pre-commit") continue;
                if (f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                if (f.Replace('\\', '/').Contains("StandaloneTests/docsymboltests/")) continue;   // not this check itself
                sb.AppendLine(File.ReadAllText(f));
            }
        }
        return sb.ToString();
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
            set.Add((hash >= 0 ? l.Substring(0, hash) : l).Trim());
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
