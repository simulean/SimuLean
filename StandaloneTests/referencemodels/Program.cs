// Headless runner of the reference models: builds every model of
// SimuLean.Testing.ReferenceModels with the real HeadlessModelFactory / handlers, runs it twice
// (determinism) and compares the statistics with baselines/<model>.json.
//
//   dotnet run                      run all models and compare (exit code 1 on any failure)
//   dotnet run -- --update          (re)write the baselines from the current engine
//   dotnet run -- --model combiner  only that model (combinable with --update)
//   dotnet run -- --tolerance 1e-4  relative tolerance (default 1e-6)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SimuLean.Testing;

static class Program
{
    static int Main(string[] args)
    {
        bool update = args.Contains("--update");
        string only = Arg(args, "--model");
        double tol = double.TryParse(Arg(args, "--tolerance"), NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
            ? t : ReferenceModelRunner.DefaultTolerance;
        string dir = Path.Combine(ProjectDir(), "baselines");
        Directory.CreateDirectory(dir);

        int failed = 0, passed = 0;
        foreach (var model in ReferenceModels.All())
        {
            if (only != null && model.Key != only) continue;
            string file = Path.Combine(dir, model.Key + ".json");
            try
            {
                var r1 = ReferenceModelRunner.Run(model.Key, model.Value);
                var r2 = ReferenceModelRunner.Run(model.Key, ReferenceModels.All().First(m => m.Key == model.Key).Value);
                var nondet = ReferenceModelRunner.Compare(ReferenceModelRunner.ToJson(r1), r2, 1e-9);   // JSON keeps 12 digits
                if (nondet.Count > 0)
                {
                    failed++;
                    Report(model.Key, "NOT DETERMINISTIC", nondet);
                    continue;
                }

                if (!update && !File.Exists(file))
                {
                    // never written silently: a deleted / renamed baseline or a new model must be committed on purpose
                    failed++;
                    Console.WriteLine($"  {model.Key,-22} FAIL no baseline {file} (run with --update and commit it)");
                    continue;
                }
                if (update)
                {
                    File.WriteAllText(file, ReferenceModelRunner.ToJson(r1) + "\n");
                    passed++;
                    Console.WriteLine($"  {model.Key,-22} baseline written ({Summary(r1)})");
                    continue;
                }

                var diffs = ReferenceModelRunner.Compare(File.ReadAllText(file), r1, tol);
                if (diffs.Count == 0) { passed++; Console.WriteLine($"  {model.Key,-22} ok   ({Summary(r1)})"); }
                else { failed++; Report(model.Key, "FAIL", diffs); }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  {model.Key,-22} ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static string Summary(ReferenceModelRunner.Result r)
    {
        var sinks = r.Stats.Where(s => s.Value.GetValueOrDefault("output") == 0 && s.Value.GetValueOrDefault("input") > 0)
                           .Select(s => $"{s.Key}={s.Value["input"]}");
        return $"{r.Elements} elements, {r.Connections} connections; in end elements: {string.Join(" ", sinks)}";
    }

    static void Report(string model, string what, List<string> diffs)
    {
        Console.WriteLine($"  {model,-22} {what}");
        foreach (var d in diffs.Take(20)) Console.WriteLine("      " + d);
        if (diffs.Count > 20) Console.WriteLine($"      … {diffs.Count - 20} more");
    }

    static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // The project folder (baselines live next to the sources, not in bin/).
    static string ProjectDir()
    {
        string d = AppContext.BaseDirectory;
        while (d != null && !File.Exists(Path.Combine(d, "referencemodels.csproj"))) d = Path.GetDirectoryName(d);
        return d ?? Directory.GetCurrentDirectory();
    }
}
