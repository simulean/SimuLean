using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SimuLean.Data;
using SimuLean.Optimization;

namespace SimuLean.Runner
{
    /// <summary>
    /// The out-of-editor runner <c>simulean</c>: the engine, the headless factory and the study runner,
    /// no Unity. JSON in (a study file: <see cref="StudySpec.Serialize"/>, or a model file alone) and JSON out (the same
    /// answer as the editor's <c>simulean_run_headless</c>, <see cref="StudySpec.Answer"/>), each carrying the exchange
    /// format and the engine fingerprint (<see cref="EngineBuild.Fingerprint"/>), so the editor never uses a stale runner.
    /// <list type="bullet">
    /// <item><c>simulean version</c> — <c>{"format", "kind": "version", "engine", "runtime"}</c>.</item>
    /// <item><c>simulean run FILE [--out RESULTS] [--threads N] [--quiet] [--cancel-file PATH]</c> — runs the study; the answer to RESULTS or
    /// the standard output; progress (<c>runs d/t</c>) on the standard error unless <c>--quiet</c>.</item>
    /// <item><c>simulean validate FILE</c> — builds and checks the study without running it.</item>
    /// <item>A batch file (<c>"kind": "batch"</c>, <see cref="StudyBatch"/>: the editor's studies with their scenarios applied)
    /// given to <c>run</c> answers the whole result set: <c>{"format", "kind": "resultset", "engine", "resultSet", "errors",
    /// "warnings", "seconds"}</c> (<see cref="Results.ResultSetJson"/>).</item>
    /// </list>
    /// What the engine logs while it runs goes to the standard error and into the answer's <c>errors</c> / <c>warnings</c>.
    /// A failure answers <c>{"format", "kind": "error", "engine", "message"}</c>; exit codes: 0 ok, 2 bad input (file,
    /// format, study), 1 any other failure.
    /// </summary>
    public static class RunnerCli
    {
        public const string Usage =
            "simulean - SimuLean studies out of the editor\n" +
            "  simulean version\n" +
            "  simulean run FILE [--out RESULTS] [--threads N] [--quiet] [--cancel-file PATH] [--stream]\n" +
            "  simulean validate FILE\n" +
            "  simulean serve          (the editor's session: batches on the standard input, one JSON line each)\n" +
            "FILE: a study file {\"format\": 1, \"kind\": \"study\", \"model\": {...}, \"study\": {...}} or a model file.";

        public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken token = default)
            => Run(args, TextReader.Null, output, error, token);

        public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken token = default)
        {
            string command = args.Length > 0 ? args[0] : "";
            try
            {
                switch (command)
                {
                    case "serve":
                        return Serve(input, output, error, token);
                    case "version":
                        output.WriteLine(MiniJson.Serialize(Envelope("version", new Dictionary<string, object>
                        {
                            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        })));
                        return 0;
                    case "run":
                    case "validate":
                        return Study(command, args.Skip(1).ToArray(), output, error, token);
                    case "":
                    case "help":
                    case "--help":
                    case "-h":
                        output.WriteLine(Usage);
                        return command == "" ? 2 : 0;
                    default:
                        throw new ArgumentException($"Unknown command '{command}'.\n{Usage}");
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is IOException || e is UnauthorizedAccessException)
            {
                return Fail(output, error, e.Message, 2);
            }
            catch (Exception e)
            {
                return Fail(output, error, e.GetType().Name + ": " + e.Message, 1);
            }
        }

        private static int Study(string command, string[] args, TextWriter output, TextWriter error, CancellationToken token)
        {
            string file = null, outFile = null, cancelFile = null;
            int? threads = null;
            bool quiet = false, stream = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--out") outFile = Value(args, ref i, a);
                else if (a == "--threads")
                    threads = int.TryParse(Value(args, ref i, a), out int n) && n >= 0 ? n : throw new ArgumentException("--threads: a whole number (0 = all cores).");
                else if (a == "--quiet") quiet = true;
                else if (a == "--cancel-file") cancelFile = Value(args, ref i, a);
                else if (a == "--stream") stream = true;
                else if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option '{a}'.\n{Usage}");
                else if (file == null) file = a;
                else throw new ArgumentException($"One file only ('{file}', then '{a}').");
            }
            if (file == null) throw new ArgumentException($"{command}: give the study file.\n{Usage}");
            string text = File.ReadAllText(file);
            // the editor cancels by creating this file (a child process cannot be sent Ctrl+C everywhere): the runs done
            // so far are still answered
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var watcher = cancelFile == null ? null : new Timer(_ => { if (File.Exists(cancelFile)) cancel.Cancel(); }, null, 200, 200))
                return Study(command, text, threads, quiet, stream, outFile, output, error, cancel.Token);
        }

        private static int Study(string command, string text, int? threads, bool quiet, bool stream, string outFile, TextWriter output, TextWriter error,
                                 CancellationToken token)
        {
            if (command == "run" && MiniJson.Parse(text) is Dictionary<string, object> root && root.TryGetValue("kind", out object k) && (k as string) == "batch")
                return Batch(StudyBatch.FromEnvelope(root), threads, quiet, stream, outFile, output, error, token);
            var (model, spec) = StudySpec.Deserialize(text);
            if (threads.HasValue) spec.Threads = threads.Value;

            var logErrors = new List<string>();
            var logWarnings = new List<string>();
            var oldWarning = SimLog.WarningSink;
            var oldError = SimLog.ErrorSink;
            SimLog.WarningSink = m => { lock (logWarnings) logWarnings.Add(m); error.WriteLine("[SimuLean:WARNING] " + m); };
            SimLog.ErrorSink = m => { lock (logErrors) logErrors.Add(m); error.WriteLine("[SimuLean:ERROR] " + m); };
            try
            {
                var clock = Stopwatch.StartNew();
                var study = spec.Build(model, out var options, out var warnings);
                if (command == "validate")
                {
                    var check = new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["elements"] = model.Elements.Count,
                        ["scenarios"] = study.Plan().Count,
                        ["metrics"] = study.Metrics.Count,
                        ["errors"] = Merge(new List<string>(), logErrors),
                        ["warnings"] = Merge(warnings, logWarnings),
                    };
                    Write(output, outFile, Envelope("validation", check));
                    return 0;
                }
                var settings = StudySpec.Settings(study);
                var results = study.Run(token, quiet ? (Action<int, int>)null : (d, t) => { lock (error) error.WriteLine($"runs {d}/{t}"); });
                options.Finish(results);
                var answer = StudySpec.Answer(results, spec.Full, warnings, settings, study.Model, spec.CompareTo);
                answer["errors"] = Merge((List<object>)answer["errors"], logErrors);
                answer["warnings"] = Merge((List<object>)answer["warnings"], logWarnings);
                answer["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 3);
                Write(output, outFile, Envelope("results", answer));
                return 0;
            }
            finally
            {
                SimLog.WarningSink = oldWarning;
                SimLog.ErrorSink = oldError;
            }
        }

        // a batch (the editor's studies, the scenarios already applied): the whole result set back
        // ── the editor's session ─────────────────────────────────────────────

        /// <summary>
        /// <c>simulean serve</c>: the runner kept alive by the editor for its session, so .NET starts
        /// and compiles once. It answers <c>{"kind": "ready", "engine", …}</c>, then reads one JSON line per request on its
        /// standard input — a batch envelope with an <c>id</c> (<c>{"format", "kind": "batch", "id", "batch"}</c>), answered
        /// as with <c>run --stream</c> (each run, then the set) with that <c>id</c> on every line; <c>{"kind": "cancel",
        /// "id"}</c> stops that batch (the runs done come back); <c>{"kind": "exit"}</c> or the end of the input ends it.
        /// Batches run one after another; a failed one answers <c>{"kind": "error", "id", "message"}</c> and the session goes on.
        /// </summary>
        private static int Serve(TextReader input, TextWriter output, TextWriter error, CancellationToken token)
        {
            var requests = new System.Collections.Concurrent.BlockingCollection<Dictionary<string, object>>();
            CancellationTokenSource current = null;
            string currentId = null;
            object gate = new object();
            var reader = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = input.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        Dictionary<string, object> request;
                        try { request = MiniJson.Parse(line) as Dictionary<string, object>; }
                        catch (FormatException e) { lock (output) { output.WriteLine(MiniJson.Serialize(Envelope("error", new Dictionary<string, object> { ["message"] = "bad request: " + e.Message }))); output.Flush(); } continue; }
                        if (request == null) continue;
                        string kind = request.TryGetValue("kind", out object k) ? k as string : null;
                        if (kind == "cancel")
                        {
                            string id = request.TryGetValue("id", out object i) ? Convert.ToString(i, System.Globalization.CultureInfo.InvariantCulture) : null;
                            lock (gate) if (current != null && id == currentId) current.Cancel();
                            continue;
                        }
                        requests.Add(request);
                        if (kind == "exit") break;
                    }
                }
                catch (IOException) { }
                finally { requests.CompleteAdding(); }
            }) { IsBackground = true };
            reader.Start();
            lock (output)
            {
                output.WriteLine(MiniJson.Serialize(Envelope("ready", new Dictionary<string, object>
                {
                    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                })));
                output.Flush();
            }
            foreach (var request in requests.GetConsumingEnumerable(token))
            {
                string kind = request.TryGetValue("kind", out object k) ? k as string : null;
                if (kind == "exit") break;
                string id = request.TryGetValue("id", out object i) ? Convert.ToString(i, System.Globalization.CultureInfo.InvariantCulture) : "";
                using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    lock (gate) { current = cancel; currentId = id; }
                    try
                    {
                        if (kind != "batch") throw new ArgumentException($"Unknown request '{kind}' (batch, cancel, exit).");
                        Batch(StudyBatch.FromEnvelope(request), null, false, true, null, output, error, cancel.Token, id);
                    }
                    catch (Exception e)
                    {
                        string message = e is ArgumentException || e is FormatException ? e.Message : e.GetType().Name + ": " + e.Message;
                        var answer = Envelope("error", new Dictionary<string, object> { ["message"] = message });
                        answer["id"] = id;
                        lock (output) { output.WriteLine(MiniJson.Serialize(answer)); output.Flush(); }
                    }
                    finally { lock (gate) { current = null; currentId = null; } }
                }
            }
            return 0;
        }

        private static int Batch(StudyBatch batch, int? threads, bool quiet, bool stream, string outFile, TextWriter output, TextWriter error,
                                 CancellationToken token, string id = null)
        {
            if (threads.HasValue) batch.Threads = threads.Value;
            var logErrors = new List<string>();
            var logWarnings = new List<string>();
            var oldWarning = SimLog.WarningSink;
            var oldError = SimLog.ErrorSink;
            SimLog.WarningSink = m => { lock (logWarnings) logWarnings.Add(m); error.WriteLine("[SimuLean:WARNING] " + m); };
            SimLog.ErrorSink = m => { lock (logErrors) logErrors.Add(m); error.WriteLine("[SimuLean:ERROR] " + m); };
            try
            {
                var clock = Stopwatch.StartNew();
                // --stream: each run on its own line as soon as it ends ({"kind": "run", "index", "run"}), the set without its
                // runs last — the editor reads the runs while the next ones are simulated
                Action<int, Results.ResultRun> runDone = null;
                if (stream)
                    runDone = (k, run) =>
                    {
                        var message = new Dictionary<string, object> { ["kind"] = "run", ["index"] = k, ["run"] = Results.ResultSetJson.RunToJson(run) };
                        if (id != null) message["id"] = id;
                        string line = MiniJson.Serialize(message);
                        lock (output) { output.WriteLine(line); output.Flush(); }
                    };
                var set = batch.Run(token, quiet ? (Action<int, int>)null : (d, t) => { lock (error) error.WriteLine($"runs {d}/{t}"); }, runDone);
                var answer = new Dictionary<string, object>
                {
                    [Results.ResultSetJson.Key] = Results.ResultSetJson.ToJson(set, withRuns: !stream),
                    ["runs"] = set.Runs.Count,
                    ["errors"] = Merge(new List<string>(), logErrors),
                    ["warnings"] = Merge(new List<string>(), logWarnings),
                    ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 3),
                };
                if (stream)
                {
                    var last = Envelope("resultset", answer);
                    if (id != null) last["id"] = id;
                    lock (output) { output.WriteLine(MiniJson.Serialize(last)); output.Flush(); }
                }
                else Write(output, outFile, Envelope("resultset", answer));
                return 0;
            }
            finally
            {
                SimLog.WarningSink = oldWarning;
                SimLog.ErrorSink = oldError;
            }
        }

        private static string Value(string[] args, ref int i, string option)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{option}: a value is missing.");
            return args[++i];
        }

        /// <summary>The answer with what was logged added (distinct, at most 50 of each).</summary>
        private static List<object> Merge<T>(List<T> answer, List<string> logged)
        {
            var all = answer.Select(x => (object)x).ToList();
            lock (logged)
                foreach (string m in logged.Distinct().Take(50))
                    if (!all.Contains(m)) all.Add(m);
            return all;
        }

        private static Dictionary<string, object> Envelope(string kind, Dictionary<string, object> body)
        {
            var d = new Dictionary<string, object>
            {
                ["format"] = Serialization.ConfigJson.FormatVersion,
                ["kind"] = kind,
                ["engine"] = EngineBuild.Fingerprint,
            };
            foreach (var kv in body) d[kv.Key] = kv.Value;
            return d;
        }

        private static void Write(TextWriter output, string outFile, Dictionary<string, object> value)
        {
            string json = MiniJson.Serialize(value);
            if (outFile == null) output.WriteLine(json);
            else File.WriteAllText(outFile, json);
        }

        private static int Fail(TextWriter output, TextWriter error, string message, int code)
        {
            error.WriteLine("simulean: " + message);
            output.WriteLine(MiniJson.Serialize(Envelope("error", new Dictionary<string, object> { ["message"] = message })));
            return code;
        }
    }
}
