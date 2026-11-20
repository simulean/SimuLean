using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SimuLean.Data;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>
    /// A study as data: the inputs of <c>simulean_run_headless</c> — horizon, replications, seed, the
    /// factors (<see cref="Factors"/>), the named metrics, explicit scenarios, a <c>where</c> filter, derived metrics,
    /// confidence, warm-up, detail — read the same way by the editor commands and the out-of-editor runner
    /// (<see cref="StudyInputs"/>, <see cref="StudyOptions"/>). <see cref="Build"/> makes the checked <see cref="Study"/> of a
    /// model; <see cref="Answer"/> is the answer both give (settings, per scenario the runs, the metrics as mean ± half-width,
    /// the model block, optionally every element's and resource's KPIs). As a file (the runner's input) it is the envelope
    /// <c>{"format": 1, "kind": "study", "model": {…}, "study": {…}}</c> (<see cref="ConfigJson"/>).
    /// </summary>
    public sealed class StudySpec
    {
        /// <summary>Horizon in model time units (−1 = the model's).</summary>
        public double Horizon = -1;
        public int Replications = 1;
        /// <summary>First seed (replications use seed, seed + 1, …).</summary>
        public int Seed = 1;
        /// <summary>Factors: JSON <c>{"path": [levels]}</c> or the shorthand <c>path=v1|v2;…</c>.</summary>
        public string Factors = "";
        /// <summary>Named metrics: JSON <c>{"name": "path"}</c> or the shorthand <c>name=path;…</c>.</summary>
        public string Metrics = "";
        public string Scenarios = "", Where = "", Derived = "";
        /// <summary>Confidence level of the half-widths (0 = 0.95).</summary>
        public double Confidence;
        /// <summary>Warm-up (−1 = the model's).</summary>
        public double Warmup = -1;
        /// <summary>End condition: stop a run when no item has moved for this time while items wait (−1 = the model's,
        /// 0 = off).</summary>
        public double NoProgress = -1;
        /// <summary><c>metrics</c> (only the named metrics and the model block) or <c>full</c> (also every element's and
        /// resource's KPIs); empty = full without metrics, else metrics.</summary>
        public string Detail = "";
        /// <summary>Worker threads (0 = <see cref="Study.DefaultThreads"/>: half the logical processors, 1 to 16).</summary>
        public int Threads = 1;
        /// <summary>The scenario the others are compared with (its name, or its 1-based position in the plan; empty = none):
        /// each other scenario's answer then carries <c>paired</c>, per metric the mean difference against it over the
        /// runs with the same seed and the half-width of that difference (<see cref="Answer"/>).</summary>
        public string CompareTo = "";

        /// <summary>Whether the answer carries every element's and resource's KPIs (<see cref="Detail"/>).</summary>
        public bool Full
        {
            get
            {
                bool full = string.Equals(Detail, "full", StringComparison.OrdinalIgnoreCase)
                            || (string.IsNullOrWhiteSpace(Detail) && string.IsNullOrWhiteSpace(Metrics));
                if (!full && !string.IsNullOrWhiteSpace(Detail) && !string.Equals(Detail, "metrics", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"detail: '{Detail}' is not 'metrics' or 'full'.");
                return full;
            }
        }

        public StudyOptions Options() => new StudyOptions
        {
            Scenarios = Scenarios ?? "", Where = Where ?? "", Derived = Derived ?? "", Confidence = Confidence, Warmup = Warmup, NoProgress = NoProgress,
        };

        /// <summary>Applies the spec to <paramref name="study"/> (a model's, or a designed experiment's when
        /// <paramref name="designed"/>: its factors and scenarios are its own, and only a horizon, replications other than 1
        /// and a seed other than 1 override its settings). Returns the options to finish the results with.</summary>
        public StudyOptions ApplyTo(Study study, bool designed = false)
        {
            _ = Full;   // an unknown detail fails before anything is applied
            if (!(Horizon > 0) && Horizon != -1)
                throw new ArgumentException($"horizon: {Horizon.ToString(CultureInfo.InvariantCulture)} — a horizon is > 0 (−1 or omitted = the model's).");
            var options = Options();
            var factors = StudyInputs.FactorsOf(Factors);
            if (designed)
            {
                if (factors.Count > 0 || !string.IsNullOrWhiteSpace(Scenarios))
                    throw new ArgumentException("experimenter: its factors and scenarios are the study's; factors / scenarios are not added to them.");
                if (Horizon > 0) study.WithRunTime(Horizon);
                if (Replications > 1) study.WithReplications(Replications);
                if (Seed != 1) study.WithSeed(Seed);
            }
            else
            {
                if (Horizon > 0) study.WithRunTime(Horizon);
                study.WithReplications(Math.Max(1, Replications)).WithSeed(Seed);
                foreach (var (path, levels) in factors) study.Factor(path, levels);
            }
            study.Threads = Threads;
            options.AddMetrics(study, StudyInputs.MetricsOf(Metrics));
            options.Apply(study, factors.Count > 0);
            // a reference that names no scenario fails before any run
            if (!string.IsNullOrWhiteSpace(CompareTo)) ScenarioNamed(study.Plan().Select(s => s.Name).ToList(), CompareTo);
            return options;
        }

        /// <summary>The scenario <paramref name="given"/> names among <paramref name="names"/>: its name (exact, else ignoring
        /// case), else its 1-based position; none = an <see cref="ArgumentException"/> listing them.</summary>
        public static string ScenarioNamed(IList<string> names, string given)
        {
            string g = (given ?? "").Trim();
            string found = names.FirstOrDefault(n => n == g) ?? names.FirstOrDefault(n => string.Equals(n, g, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
            if (int.TryParse(g, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) && k >= 1 && k <= names.Count) return names[k - 1];
            throw new ArgumentException($"compareTo: no scenario '{given}' — give a scenario's name or its position 1..{names.Count}: " +
                                        string.Join(", ", names.Take(20).Select((n, i) => $"{i + 1} = '{n}'")) + (names.Count > 20 ? ", …" : "") + "." +
                                        Serialization.NameSuggestions.DidYouMean(g, names));
        }

        /// <summary>The study of <paramref name="model"/>, checked on the built model before any run (<see cref="Study.Check"/>):
        /// an error refuses it (all its errors in the message); the warnings are returned.</summary>
        public Study Build(SimulationConfig model, out StudyOptions options, out List<string> warnings)
        {
            var study = new Study(model);
            options = ApplyTo(study);
            warnings = new List<string>(Checked(study));
            warnings.AddRange(options.Warnings);
            return study;
        }

        /// <summary>Checks a study: its errors refuse it (<see cref="ArgumentException"/>), its warnings are returned.</summary>
        public static List<string> Checked(Study study)
        {
            var check = study.Check();
            if (!check.Ok)
                throw new ArgumentException("The study cannot run: " + string.Join(" | ", check.Errors) +
                                            (check.Warnings.Count > 0 ? " Warnings: " + string.Join(" | ", check.Warnings) : ""));
            return check.Warnings;
        }

        // ── the answer ───────────────────────────────────────────────────────

        private static readonly string[] ElementKpis =
            { ResultMetrics.Input, ResultMetrics.Output, ResultMetrics.ContentAvg, ResultMetrics.ContentMax, ResultMetrics.StayAvg, ResultMetrics.Utilization };
        private static readonly string[] ResourceKpis = { ResultMetrics.Utilization, ResultMetrics.Tasks };

        /// <summary>The end condition "no progress" as the settings and the model description name it.</summary>
        public static string NoProgressText(double t)
            => $"no item moved for {t.ToString("G6", CultureInfo.InvariantCulture)} while items wait";

        /// <summary>
        /// The effective settings of a study's runs: the model's time unit, the horizon actually run, the
        /// warm-up, the end condition, the seeds and replications — so a default is never silent. Explicit scenarios that
        /// carry their own replications are echoed as they run (a single count when they agree, else the largest plus
        /// <c>replicationsPerScenario</c>), not as the study's default.
        /// </summary>
        public static Dictionary<string, object> Settings(Study study)
        {
            var m = study.Model;
            string end = null;
            if (m.StopAt != null && m.StopAt.Count > 0 && m.StopAtCount > 0)
                end = $"{m.StopAtCount} items received by {string.Join(", ", m.StopAt)}";
            if (m.StopWhenEmpty) end = (end != null ? end + "; " : "") + "nothing left to do (or a deadlock / stall)";
            if (m.StopWhenNoProgressFor > 0) end = (end != null ? end + "; " : "") + NoProgressText(m.StopWhenNoProgressFor);
            // the replications each explicit scenario runs (its own, or the study's), as Batch gives them
            int reps = Math.Max(1, study.Replications);
            Dictionary<string, object> perScenario = null;
            if (study.Scenarios.Count > 0)
            {
                var counts = study.Scenarios.Select(s => Math.Max(1, s.Replications > 0 ? s.Replications : study.Replications)).ToList();
                reps = counts.Max();
                if (counts.Distinct().Count() > 1)
                {
                    perScenario = new Dictionary<string, object>();
                    for (int i = 0; i < counts.Count; i++) perScenario[study.Scenarios[i].Name ?? $"#{i + 1}"] = counts[i];
                }
            }
            var settings = new Dictionary<string, object>
            {
                ["timeUnit"] = ResultMetrics.TimeUnitName(m.TimeUnitSeconds > 0 ? m.TimeUnitSeconds : 1.0),
                ["secondsPerTimeUnit"] = m.TimeUnitSeconds > 0 ? m.TimeUnitSeconds : 1.0,
                ["horizon"] = study.EffectiveRunTime,
                ["warmup"] = m.WarmupTime,
                // the calendar date of time 0 when the model has one (shifts and timetables are read against it)
                ["start"] = string.IsNullOrWhiteSpace(m.StartDateTime) ? null : m.StartDateTime.Trim(),
                ["endCondition"] = end,
                ["replications"] = reps,
                ["seeds"] = SeedRange(study.BaseSeed, reps),
                ["confidence"] = study.Confidence,
            };
            if (perScenario != null) settings["replicationsPerScenario"] = perScenario;
            return settings;
        }

        /// <summary>The seeds of <paramref name="replications"/> runs from <paramref name="first"/>: "1..3", or "1".</summary>
        private static string SeedRange(int first, int replications)
            => replications > 1 ? $"{first}..{first + replications - 1}" : first.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The answer of a study: the effective <paramref name="settings"/>; per scenario its runs ok / failed
        /// and the first error when one failed — a failed run is never averaged in nor reported as a null metric —, the end
        /// reasons counted (<see cref="EndReasonCounts"/>) with the runs that <c>deadlocked</c> / <c>stalled</c> when any did,
        /// each metric as mean ± half-width over the completed runs (<c>n</c> = the runs that gave a number), and the model
        /// block when a run deadlocked, stalled or ended on no progress. The compact answer (not <paramref name="full"/>)
        /// gives its numbers to <see cref="CompactDigits"/> significant digits; with <paramref name="full"/> every digit, the
        /// model block always, the elements' and resources' KPIs (means) and <c>perRun</c> (each run's seed, end reason and
        /// metric values, or its error). A large answer is paged by <see cref="AnswerPages.Study"/>. <c>errors</c> lists the
        /// scenarios with failed runs; <c>warnings</c> the model's own warnings of the runs (<see cref="ModelWarningNotes"/>).
        /// With the study's <paramref name="model"/>, a Queue left at the default capacity that a
        /// run filled is noted in <c>warnings</c> (<see cref="DefaultCapacityNotes"/>; no number changes). With
        /// <paramref name="full"/> each metric also gives its <c>min</c> / <c>max</c> over the runs (two runs or more). With
        /// <paramref name="compareTo"/> (<see cref="CompareTo"/>) every other scenario carries <c>paired</c>: per metric the
        /// difference to that scenario on common random numbers (<see cref="PairedOf"/>).
        /// </summary>
        public static Dictionary<string, object> Answer(ResultSet resultSet, bool full, List<string> warnings, Dictionary<string, object> settings = null,
                                                        SimulationConfig model = null, string compareTo = null)
        {
            var errors = new List<string>();
            warnings = new List<string>(warnings ?? new List<string>());
            if (resultSet.Metadata.Any(kv => kv.Key == "cancelled"))
                warnings.Add("The study was cancelled: the scenarios hold the runs done so far.");
            warnings.AddRange(ModelWarningNotes(resultSet));
            if (model != null) warnings.AddRange(DefaultCapacityNotes(model, resultSet));
            warnings.AddRange(IdenticalLevelNotes(resultSet));
            string reference = null;
            if (!string.IsNullOrWhiteSpace(compareTo))
            {
                try { reference = ScenarioNamed(resultSet.Scenarios.Select(s => s.Name).ToList(), compareTo); }
                catch (ArgumentException e) { warnings.Add(e.Message); }
            }
            var referenceRuns = reference == null ? null
                : resultSet.Runs.Where(r => r.Scenario == reference && r.Error == null).GroupBy(r => r.Seed).ToDictionary(g => g.Key, g => g.First());
            var scenarios = resultSet.Scenarios.Select(sc =>
            {
                var runs = resultSet.Runs.Where(r => r.Scenario == sc.Name).ToList();
                var ok = runs.Where(r => r.Error == null).ToList();
                var failed = runs.Where(r => r.Error != null).ToList();
                if (failed.Count > 0)
                    errors.Add($"Scenario '{sc.Name}': {failed.Count} of {runs.Count} runs failed (seed {failed[0].Seed}): {failed[0].Error}");
                var results = ok.Where(r => r.Results != null).Select(r => r.Results).ToList();
                var summary = results.Count > 0 ? ResultSummary.Of(results) : null;
                // the runs that deadlocked or stalled, also when the run went on to its horizon (a periodic event)
                int deadlocked = results.Count(r => ModelFlag(r, ResultMetrics.Deadlocked) || r.Info.Deadlocked);
                int stalled = results.Count(r => ModelFlag(r, ResultMetrics.Stalled));
                var answer = new Dictionary<string, object>
                {
                    ["scenario"] = sc.Name,
                    ["parameters"] = sc.Parameters.ToDictionary(p => p.Key, p => TypedLevel(p.Value)),
                    ["runs"] = new Dictionary<string, object> { ["ok"] = ok.Count, ["failed"] = failed.Count },
                };
                if (failed.Count > 0) answer["firstError"] = failed[0].Error;
                answer["endReasons"] = EndReasonCounts(results);
                if (deadlocked > 0) answer["deadlocked"] = deadlocked;
                if (stalled > 0) answer["stalled"] = stalled;
                answer["metrics"] = resultSet.MetricNames.ToDictionary(m => m, m => (object)Shown(IntervalOf(ok.Select(r => r.Metrics.TryGetValue(m, out double v) ? v : double.NaN), resultSet.Confidence, full), full));
                // the model block (end time, makespan, deadlock and stall times): in the compact answer only when a run
                // deadlocked, stalled or ended on no progress — it says when and for how long; detail=full always
                bool halted = deadlocked > 0 || stalled > 0 || results.Any(r => r.Info.EndReason == nameof(RunEndReason.NoProgress));
                if (summary != null && (full || halted)) answer["model"] = Shown(Stats(summary.Of(ResultScope.Model).FirstOrDefault(), null), full);
                if (reference != null && sc.Name != reference)
                    answer["paired"] = new Dictionary<string, object>
                    {
                        ["to"] = reference,
                        ["metrics"] = resultSet.MetricNames.ToDictionary(m => m, m => (object)Shown(PairedOf(ok, referenceRuns, m, resultSet.Confidence), full)),
                    };
                // who waited for what in the first run that stalled or deadlocked: simulean_diagnose shows the whole state
                var stuck = results.FirstOrDefault(r => r.Info.Waiting.Count > 0);
                if (stuck != null)
                    answer["waiting"] = new Dictionary<string, object>
                    {
                        ["seed"] = ok.First(r => r.Results == stuck).Seed,
                        ["items"] = stuck.Info.Waiting.Take(20).Cast<object>().ToList(),
                    };
                if (full)
                {
                    answer["elements"] = summary?.Of(ResultScope.Element).Select(o => (object)new Dictionary<string, object>
                        { ["name"] = o.Name, ["type"] = o.Type, ["stats"] = Stats(o, ElementKpis) }).ToList();
                    answer["resources"] = summary?.Of(ResultScope.Resource).Select(o => (object)new Dictionary<string, object>
                        { ["name"] = o.Name, ["team"] = o.Group, ["stats"] = Stats(o, ResourceKpis) }).ToList();
                    answer["perRun"] = runs.Select(PerRun).ToList();
                }
                return (object)answer;
            }).ToList();
            return new Dictionary<string, object>
            {
                ["settings"] = settings,
                ["scenarios"] = scenarios,
                ["errors"] = errors.Cast<object>().ToList(),
                ["warnings"] = warnings.Cast<object>().ToList(),
            };
        }

        /// <summary>How many of <paramref name="results"/> ended for each reason (<see cref="RunEndReason"/>), in the order
        /// they first occur: <c>{"Horizon": 20}</c> — the per-run reasons are in <c>perRun</c> (detail=full).</summary>
        public static Dictionary<string, object> EndReasonCounts(IEnumerable<RunResults> results)
        {
            var counts = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var r in results)
            {
                string reason = r.Info.EndReason ?? "";
                counts[reason] = counts.TryGetValue(reason, out object n) ? (int)n + 1 : 1;
            }
            return counts;
        }

        // a 0 / 1 statistic of the run's model object (deadlocked, stalled)
        private static bool ModelFlag(RunResults r, string metric)
            => r.Find(ResultScope.Model, "model") is ResultObject o && o.TryGet(metric, out double v) && v > 0;

        /// <summary>One run of a scenario as <c>perRun</c> gives it (detail=full): its seed and replication, then its end
        /// reason and its named metrics at full precision, or its error.</summary>
        private static object PerRun(ResultRun r)
        {
            var d = new Dictionary<string, object> { ["seed"] = r.Seed, ["replication"] = r.Replication };
            if (r.Error != null) { d["error"] = r.Error; return d; }
            if (r.Results != null) d["endReason"] = r.Results.Info.EndReason;
            d["metrics"] = r.Metrics.ToDictionary(kv => kv.Key, kv => Finite(kv.Value));
            return d;
        }

        /// <summary>The significant digits of a number in the compact answer (detail=metrics); detail=full and the exports
        /// keep every digit.</summary>
        public const int CompactDigits = 6;

        // a block of numbers as the answer shows it: as is with detail=full, else each double to CompactDigits
        private static Dictionary<string, object> Shown(Dictionary<string, object> block, bool full)
        {
            if (full || block == null) return block;
            foreach (string k in block.Keys.ToList())
                if (block[k] is double v) block[k] = Significant(v, CompactDigits);
            return block;
        }

        /// <summary><paramref name="v"/> to <paramref name="digits"/> significant digits (a number that prints short).</summary>
        public static double Significant(double v, int digits)
        {
            if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return v;
            return double.Parse(v.ToString("G" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        /// <summary>A factor level as the answer echoes it: <c>true</c> / <c>false</c> as a bool, a finite number as a
        /// number, anything else (an option, a file, a list) as its text.</summary>
        public static object TypedLevel(string level)
        {
            string t = (level ?? "").Trim();
            if (bool.TryParse(t, out bool b)) return b;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) && !double.IsInfinity(d)) return d;
            return level;
        }

        /// <summary>
        /// One note per factor whose every level gave the same results: within each group of scenarios that agree on the
        /// other factors, the levels' metric means are identical (with common random numbers a factor that never binds —
        /// a capacity never reached — changes nothing, bit for bit). For a <c>Element.capacity</c> factor the note adds the
        /// element's largest <c>content_max</c> when it stayed below the smallest level. None without named metrics, with a
        /// single scenario or a scenario without completed runs.
        /// </summary>
        public static List<string> IdenticalLevelNotes(ResultSet resultSet)
        {
            var notes = new List<string>();
            if (resultSet?.Scenarios == null || resultSet.Scenarios.Count < 2 || resultSet.MetricNames.Count == 0) return notes;
            var means = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var sc in resultSet.Scenarios)
            {
                var ok = resultSet.Runs.Where(r => r.Scenario == sc.Name && r.Error == null).ToList();
                if (ok.Count == 0 || means.ContainsKey(sc.Name ?? "")) return notes;
                means[sc.Name ?? ""] = resultSet.MetricNames.Select(m =>
                {
                    var v = ok.Select(r => r.Metrics.TryGetValue(m, out double x) ? x : double.NaN).Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
                    return v.Count == 0 ? double.NaN : v.Average();
                }).ToArray();
            }
            bool Same(double[] a, double[] b)
            {
                for (int i = 0; i < a.Length; i++) if (!(a[i] == b[i] || (double.IsNaN(a[i]) && double.IsNaN(b[i])))) return false;
                return true;
            }
            string ValueOf(ResultScenario sc, string key) => sc.Parameters.FirstOrDefault(p => p.Key == key).Value;
            var keys = resultSet.Scenarios.SelectMany(sc => sc.Parameters.Select(p => p.Key)).Distinct().ToList();
            foreach (string key in keys)
            {
                bool compared = false, differs = false;
                var groups = resultSet.Scenarios.GroupBy(sc => string.Join("", keys.Where(k => k != key).Select(k => k + "=" + ValueOf(sc, k))));
                foreach (var g in groups)
                {
                    var list = g.ToList();
                    if (list.Select(sc => ValueOf(sc, key)).Distinct().Count() < 2) continue;
                    compared = true;
                    if (list.Any(sc => !Same(means[sc.Name ?? ""], means[list[0].Name ?? ""]))) { differs = true; break; }
                }
                if (!compared || differs) continue;
                var levels = resultSet.Scenarios.Select(sc => ValueOf(sc, key)).Where(v => v != null).Distinct().ToList();
                notes.Add($"Factor '{key}': every level ({string.Join("|", levels)}) gave the same results" + CapacityNote(resultSet, key, levels) +
                          " — the factor may not bind (with common random numbers a change that does nothing gives identical runs).");
            }
            return notes;
        }

        // " (content_max 1 never reached the smallest capacity, 2)" for an element capacity factor whose element never filled
        private static string CapacityNote(ResultSet resultSet, string key, List<string> levels)
        {
            const string suffix = ".capacity";
            if (!key.EndsWith(suffix, StringComparison.Ordinal) || key.Contains(":")) return "";
            string owner = key.Substring(0, key.Length - suffix.Length);
            var numbers = levels.Select(l => double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN).ToList();
            if (numbers.Count == 0 || numbers.Any(double.IsNaN)) return "";
            double max = double.NaN;
            foreach (var r in resultSet.Runs)
                if (r.Results?.Find(ResultScope.Element, owner) is ResultObject o && o.TryGet(ResultMetrics.ContentMax, out double c) && !(c <= max)) max = c;
            if (double.IsNaN(max) || max >= numbers.Min()) return "";
            return $" ({owner}.{ResultMetrics.ContentMax} {max.ToString("G6", CultureInfo.InvariantCulture)} never reached the smallest capacity, " +
                   $"{numbers.Min().ToString("G6", CultureInfo.InvariantCulture)})";
        }

        /// <summary>At most this many of the models' own warnings in a study's answer (<see cref="ModelWarningNotes"/>).</summary>
        public const int MaxModelWarnings = 10;

        /// <summary>
        /// The warnings the model gave while it ran (<see cref="RunInfo.Warnings"/>), carried in the runs' results — a run
        /// outside the editor logs them where the answer does not read. One line per cause: the same warning in several runs
        /// (differing only in its numbers: the time, an item's id) is its first text, with "(and in k more run(s) of this
        /// answer, at other times)" as the command's log gives a repeated deadlock; the first <see cref="MaxModelWarnings"/>,
        /// then a count of the others. None when the runs gave none.
        /// </summary>
        public static List<string> ModelWarningNotes(ResultSet resultSet)
        {
            var groups = new List<(string key, List<string> texts)>();
            foreach (var run in resultSet?.Runs ?? new List<ResultRun>())
            {
                if (run.Results == null) continue;
                // the k-th warning of a kind in one run is another cause (another element): kept apart
                var inRun = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string w in run.Results.Info.Warnings)
                {
                    if (string.IsNullOrWhiteSpace(w)) continue;
                    string kind = System.Text.RegularExpressions.Regex.Replace(w, @"\d+(\.\d+)?", "#");
                    inRun[kind] = inRun.TryGetValue(kind, out int k) ? k + 1 : 0;
                    string key = kind + "\u0001" + inRun[kind];
                    var g = groups.FirstOrDefault(x => x.key == key);
                    if (g.texts == null) groups.Add(g = (key, new List<string>()));
                    if (!g.texts.Contains(w)) g.texts.Add(w);
                }
            }
            var notes = groups.Take(MaxModelWarnings)
                              .Select(g => g.texts.Count > 1 ? g.texts[0] + $" (and in {g.texts.Count - 1} more run(s) of this answer, at other times)" : g.texts[0])
                              .ToList();
            if (groups.Count > MaxModelWarnings)
                notes.Add($"… and {groups.Count - MaxModelWarnings} more model warning(s): simulean_diagnose lists a run's.");
            return notes;
        }

        /// <summary>
        /// The Queues of <paramref name="model"/> left at the default capacity (<see cref="QueueSettings.Capacity"/>, 100) that
        /// some run filled (<c>content_max</c> reached it): one note each — "there is no unlimited Queue" is easy to miss and
        /// the default then caps the flow silently (a stall, a deadlock, a blocked feeder). A scenario whose factors set the
        /// Queue's capacity is not counted (that capacity was chosen). Read from the runs' statistics only.
        /// </summary>
        public static List<string> DefaultCapacityNotes(SimulationConfig model, ResultSet resultSet)
        {
            var notes = new List<string>();
            if (model?.Elements == null || resultSet == null) return notes;
            int byDefault = new QueueSettings().Capacity;
            foreach (var e in model.Elements)
            {
                if (e?.Type != "Queue" || string.IsNullOrEmpty(e.Name) || !(e.SettingsAs<QueueSettings>() is QueueSettings q) || q.Capacity != byDefault)
                    continue;
                string key = e.Name + ".capacity";
                var chosen = new HashSet<string>(resultSet.Scenarios
                    .Where(sc => sc.Parameters.Any(p => string.Equals(p.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase)))
                    .Select(sc => sc.Name));
                int runs = 0, full = 0;
                foreach (var r in resultSet.Runs)
                {
                    if (r.Results == null || chosen.Contains(r.Scenario)) continue;
                    runs++;
                    var o = r.Results.Find(ResultScope.Element, e.Name);
                    if (o != null && o.TryGet(ResultMetrics.ContentMax, out double max) && max >= byDefault) full++;
                }
                if (full > 0)
                    notes.Add($"Queue '{e.Name}' reached its capacity {byDefault}, the default ({ResultMetrics.ContentMax} = {byDefault} in {full} of {runs} runs): " +
                              "raise it if it is meant to be unlimited (there is no unlimited value: e.g. capacity = 1000000).");
            }
            return notes;
        }

        private static Dictionary<string, object> Stats(SummaryObject o, string[] only)
        {
            var d = new Dictionary<string, object>();
            if (o == null) return d;
            foreach (string m in only ?? o.Metrics.ToArray())
                if (o.TryGet(m, out StatSummary s)) d[m] = Finite(s.Mean);
            return d;
        }

        /// <summary>A metric over the replications: mean, half-width of the <paramref name="confidence"/> interval (null with
        /// fewer than two values) and n, the finite values (a time not reached is NaN and left out); with
        /// <paramref name="range"/> and two values or more also their <c>min</c> and <c>max</c>.</summary>
        public static Dictionary<string, object> IntervalOf(IEnumerable<double> values, double confidence, bool range = false)
        {
            var v = values.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
            if (v.Count == 0) return new Dictionary<string, object> { ["mean"] = null, ["halfWidth"] = null, ["n"] = 0 };
            var st = StatSummary.Of(v, confidence);
            var d = new Dictionary<string, object> { ["mean"] = Finite(st.Mean), ["halfWidth"] = st.HasInterval ? Finite(st.HalfWidth) : null, ["n"] = v.Count };
            if (range && v.Count > 1) { d["min"] = Finite(st.Min); d["max"] = Finite(st.Max); }
            return d;
        }

        /// <summary>
        /// A metric of a scenario against the reference scenario on common random numbers: each run of
        /// <paramref name="runs"/> paired with the reference's run of the same seed (<paramref name="reference"/>, by seed),
        /// the differences this − reference; <c>diff</c> their mean, <c>halfWidth</c> the half-width of its
        /// <paramref name="confidence"/> interval (null with fewer than two pairs), <c>n</c> the pairs with a number on both
        /// sides. The runs of one seed share their random streams, so their noise largely cancels: the paired half-width is
        /// usually much smaller than the two scenarios' own, and a difference whose interval excludes 0 is significant.
        /// </summary>
        public static Dictionary<string, object> PairedOf(IEnumerable<ResultRun> runs, IReadOnlyDictionary<int, ResultRun> reference, string metric, double confidence)
        {
            var diffs = new List<double>();
            foreach (var r in runs)
            {
                if (r?.Error != null || !reference.TryGetValue(r.Seed, out var b)) continue;
                if (!r.Metrics.TryGetValue(metric, out double x) || !b.Metrics.TryGetValue(metric, out double y)) continue;
                double d = x - y;
                if (!double.IsNaN(d) && !double.IsInfinity(d)) diffs.Add(d);
            }
            if (diffs.Count == 0) return new Dictionary<string, object> { ["diff"] = null, ["halfWidth"] = null, ["n"] = 0 };
            var st = StatSummary.Of(diffs, confidence);
            return new Dictionary<string, object> { ["diff"] = Finite(st.Mean), ["halfWidth"] = st.HasInterval ? Finite(st.HalfWidth) : null, ["n"] = diffs.Count };
        }

        private static object Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : (object)v;

        // ── as a file ────────────────────────────────────────────────────────

        /// <summary>The runner's input: the model and the study, <c>{"format": 1, "kind": "study", "model": {…}, "study": {…}}</c>.</summary>
        public static string Serialize(SimulationConfig model, StudySpec spec)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var sb = new StringBuilder(4096);
            sb.Append("{\"format\":").Append(ConfigJson.FormatVersion).Append(",\"kind\":\"study\",\"model\":");
            ConfigJson.Write(sb, model, typeof(SimulationConfig), "model");
            sb.Append(",\"study\":");
            ConfigJson.Write(sb, spec ?? new StudySpec(), typeof(StudySpec), "study");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Reads a study file (<see cref="Serialize"/>); a model file alone is a study of one run with the defaults.</summary>
        public static (SimulationConfig model, StudySpec spec) Deserialize(string json)
        {
            if (!(MiniJson.Parse(json ?? throw new ArgumentNullException(nameof(json))) is Dictionary<string, object> root))
                throw new FormatException("A study file is a JSON object {\"format\": …, \"kind\": \"study\", \"model\": {…}, \"study\": {…}}.");
            string kind = root.TryGetValue("kind", out object k) ? k as string : null;
            ConfigJson.CheckEnvelope(root, kind == "model" ? "model" : "study");
            if (!root.TryGetValue("model", out object m) || !(m is Dictionary<string, object>))
                throw new FormatException("The file has no \"model\" object.");
            var model = (SimulationConfig)ConfigJson.Read(m, typeof(SimulationConfig), "model");
            var spec = root.TryGetValue("study", out object s) && s != null
                ? (StudySpec)ConfigJson.Read(s, typeof(StudySpec), "study") : new StudySpec();
            return (model, spec);
        }
    }
}
