using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimuLean.Data;
using SimuLean.Optimization;
using SimuLean.Results;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The out-of-editor runner seen from the editor: studies and optimisations run on .NET (≈ 10× faster
    /// per run than on Unity's Mono, and without Mono's stop-the-world collector) in a separate process,
    /// <c>Tools/Runner</c>'s <c>simulean</c>.
    /// <list type="bullet">
    /// <item><b>Where:</b> first the runner shipped with the package, <c>SimuLeanRunner/&lt;rid&gt;/simulean[.exe]</c> at the
    /// project root (built by the release for its engine: no wait); else <c>Library/SimuLeanRunner/&lt;engine fingerprint&gt;/simulean[.exe]</c> — one build per engine
    /// version. When it is missing and the project has the runner's sources and a .NET 10 SDK, it is built there in the
    /// background (<c>dotnet publish</c>, self-contained); meanwhile, and whenever it cannot be used, the work runs in the
    /// editor (<see cref="Executor.Note"/> says why).</item>
    /// <item><b>Never stale:</b> the runner's <c>engine</c> fingerprint must be the editor's
    /// (<see cref="EngineFingerprint.OfSources"/> of <c>Assets/SimuLean.Net</c>), checked once per editor session.</item>
    /// <item><b>What:</b> a <see cref="StudyBatch"/> (the scenarios applied in the editor), written as JSON; the whole
    /// <see cref="ResultSet"/> comes back (<see cref="ResultSetJson"/>); progress and the engine's log arrive on the
    /// runner's standard error; cancelling creates the cancel file (the runs done so far come back).</item>
    /// <item><b>Switch:</b> <see cref="SimuLeanProjectSettings.runOutsideEditor"/> (on by default; the SimuLean window ▸
    /// Settings). Editor only: a player runs in process.</item>
    /// </list>
    /// </summary>
    public static class ExternalRunner
    {
        private static readonly object gate = new object();
        private static string fingerprint, verifiedPath, unavailable, shippedRejected;
        private static Task building;

        /// <summary>
        /// Where the work of one study runs, decided on the main thread (it reads the project): the runner's path, or null
        /// with the reason in <see cref="Note"/>. <see cref="Run"/> may then be called from any thread.
        /// </summary>
        public sealed class Executor
        {
            /// <summary>The runner's executable, null = in the editor.</summary>
            public string RunnerPath { get; internal set; }
            /// <summary>Why the work runs in the editor (null when the runner runs it, or when it is switched off).</summary>
            public string Note { get; internal set; }
            internal string JobsFolder;

            /// <summary>"runner" or "editor": where the last <see cref="Run"/> ran.</summary>
            public string RanIn { get; private set; } = "editor";

            /// <summary>Runs <paramref name="batch"/>: in the runner when there is one (falling back to the editor, with a
            /// note, when it fails to answer), else in the editor.</summary>
            public ResultSet Run(StudyBatch batch, CancellationToken token = default, Action<int, int> progress = null)
            {
                if (RunnerPath != null)
                {
                    try
                    {
                        var set = RunExternal(RunnerPath, JobsFolder, batch, token, progress);
                        RanIn = "runner";
                        set.Metadata.Add(new KeyValuePair<string, string>("ran_in", Description));
                        return set;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        Note = "the runner failed (" + e.Message + "); the study ran in the editor";
                        SimLog.Warning("[SimuLean] Runner: " + Note + ".");
                    }
                }
                RanIn = "editor";
                var local = batch.Run(token, progress);
                local.Metadata.Add(new KeyValuePair<string, string>("ran_in", Description));
                return local;
            }

            /// <summary>Runs <paramref name="study"/> (its scenarios applied here: <see cref="Study.Batch"/>).</summary>
            public ResultSet Run(Study study, CancellationToken token = default, Action<int, int> progress = null)
                => Run(study.Batch(), token, progress);

            /// <summary>Where it ran, for an answer: <c>runner</c> or <c>editor</c> with the reason.</summary>
            public string Description => RanIn == "runner" ? "runner (.NET, out of the editor)" : "editor" + (Note != null ? " — " + Note : "");
        }

        /// <summary>While true, every study runs in the editor (the EditMode tests: their results must not depend on whether
        /// the runner is built yet; the runner's own tests use <see cref="Prepare(bool)"/>).</summary>
        public static volatile bool Suspended;

        /// <summary>Main thread: where the next study runs (and starts building the runner when it can be built).</summary>
        public static Executor Prepare() => Prepare(false);

        /// <param name="force">Use the runner even when switched off, suspended or in batch mode (the runner's tests).</param>
        public static Executor Prepare(bool force)
        {
            if (!force)
            {
                if (Suspended || Application.isBatchMode) return new Executor();
                if (!SimuLeanProjectSettings.Load().runOutsideEditor) return new Executor();
            }
            if (!Application.isEditor) return new Executor { Note = "a player runs studies in process" };
            string root = ResultsOutput.ProjectRoot;
            string path = Locate(root, out string reason);
            return new Executor { RunnerPath = path, Note = path == null ? reason : null, JobsFolder = Path.Combine(root, "Library", "SimuLeanRunner", "jobs") };
        }

        /// <summary>
        /// Main thread: the runner of this engine version, made sure of — located, or its build started as a study's first
        /// use starts it (the same build) —; the task ends with null when it is ready, or with why there is none (the build
        /// failed, no .NET SDK, no runner sources). Done at once when the runner is already there or none can be built.
        /// The EditMode runner waits for it before a run that includes the runner's own tests, so they never find the build
        /// still going.
        /// </summary>
        public static Task<string> EnsureBuilt()
        {
            if (!Application.isEditor) return Task.FromResult("a player runs studies in process");
            if (Locate(ResultsOutput.ProjectRoot, out string reason) != null) return Task.FromResult<string>(null);
            Task build;
            lock (gate) build = building;
            if (build == null || build.IsCompleted) return Task.FromResult(reason);
            return build.ContinueWith(_ => { lock (gate) return unavailable; }, TaskScheduler.Default);
        }

        /// <summary>The editor's engine fingerprint (sources under <c>Assets/SimuLean.Net</c> and the choice rules under
        /// <c>Assets/SimuLean.UserLogic</c>; once per editor session).</summary>
        public static string Fingerprint(string projectRoot)
        {
            lock (gate)
                return fingerprint ?? (fingerprint = EngineFingerprint.OfSources(Path.Combine(projectRoot, "Assets", "SimuLean.Net"),
                                                                                 Path.Combine(projectRoot, "Assets", "SimuLean.UserLogic")));
        }

        /// <summary>The runner folder of this engine version.</summary>
        public static string Folder(string projectRoot)
            => Path.Combine(projectRoot, "Library", "SimuLeanRunner", Fingerprint(projectRoot) + RunnerSourcesSuffix(projectRoot));

        private static string runnerSources;

        // the runner's own sources and build settings (Tools/Runner) also name the build: a change to them alone (its
        // protocol, the collector, ready-to-run) must not leave the old build in use
        private static string RunnerSourcesSuffix(string projectRoot)
        {
            lock (gate)
            {
                if (runnerSources != null) return runnerSources;
                string dir = Path.Combine(projectRoot, "Tools", "Runner");
                if (!Directory.Exists(dir)) return runnerSources = "";
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    foreach (string f in Directory.GetFiles(dir).Where(f => f.EndsWith(".cs") || f.EndsWith(".csproj") || f.EndsWith(".targets"))
                                                                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
                    {
                        byte[] b = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(f) + "\n" + File.ReadAllText(f).Replace("\r", "") + "\n");
                        sha.TransformBlock(b, 0, b.Length, null, 0);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return runnerSources = "-" + string.Concat(sha.Hash.Take(4).Select(x => x.ToString("x2")));
                }
            }
        }

        private static string ExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "simulean.exe" : "simulean";

        private static string Rid
        {
            get
            {
                bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return arm ? "win-arm64" : "win-x64";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return arm ? "osx-arm64" : "osx-x64";
                return arm ? "linux-arm64" : "linux-x64";
            }
        }

        private static string Locate(string root, out string reason)
        {
            reason = null;
            string fp;
            try { fp = Fingerprint(root); }
            catch (Exception e) { reason = "the engine sources cannot be read (" + e.Message + ")"; return null; }
            // a runner shipped with the package (SimuLeanRunner/<rid>/, built by the release for this engine): no wait
            string shipped = Path.Combine(root, "SimuLeanRunner", Rid, ExeName);
            lock (gate)
                if (verifiedPath == shipped && File.Exists(shipped)) return shipped;
            bool checkShipped;
            lock (gate) checkShipped = shippedRejected != shipped;
            if (checkShipped && File.Exists(shipped))
            {
                if (VersionOf(shipped, out _) == fp)
                {
                    lock (gate) verifiedPath = shipped;
                    return shipped;
                }
                lock (gate) shippedRejected = shipped;   // another engine version: built here instead (asked once)
            }
            string exe = Path.Combine(Folder(root), ExeName);
            lock (gate)
            {
                if (verifiedPath == exe && File.Exists(exe)) return exe;
                if (building != null && !building.IsCompleted) { reason = "the runner is being built for this engine version (first use)"; return null; }
                if (unavailable != null) { reason = unavailable; return null; }
            }
            if (File.Exists(exe))
            {
                string engine = VersionOf(exe, out string error);
                if (engine == fp)
                {
                    lock (gate) verifiedPath = exe;
                    return exe;
                }
                reason = engine == null ? "the runner does not answer (" + error + ")" : $"the runner was built from another engine ({engine}, the editor's is {fp})";
                try { Directory.Delete(Folder(root), true); } catch { /* rebuilt below */ }
            }
            string project = Path.Combine(root, "Tools", "Runner", "simulean.csproj");
            if (!File.Exists(project))
            {
                lock (gate) unavailable = "no runner for this engine version (and no runner sources to build it in this project)";
                reason = unavailable;
                return null;
            }
            StartBuild(project, Folder(root), fp);
            reason = "the runner is being built for this engine version (first use)";
            return null;
        }

        private static void StartBuild(string project, string folder, string fp)
        {
            lock (gate)
            {
                if (building != null && !building.IsCompleted) return;
                building = Task.Run(() =>
                {
                    string output;
                    int code = Execute("dotnet", $"publish \"{project}\" -c Release -r {Rid} --self-contained true -p:PublishSingleFile=true " +
                                                 $"-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true -o \"{folder}\" --nologo -v quiet",
                                       300000, out output);
                    string exe = Path.Combine(folder, ExeName);
                    if (code == 0 && VersionOf(exe, out _) == fp)
                    {
                        // the builds of earlier versions (≈ 40 MB each) go
                        foreach (string old in Directory.GetDirectories(Path.GetDirectoryName(folder)))
                            if (old != folder && Path.GetFileName(old) != "jobs")
                                try { Directory.Delete(old, true); } catch { /* in use: next time */ }
                        SimLog.Info("[SimuLean] Runner built for this engine version: studies run outside the editor.");
                        return;
                    }
                    lock (gate)
                        unavailable = code == int.MinValue
                            ? "no .NET 10 SDK to build the runner (dotnet not found)"
                            : "the runner could not be built (" + Tail(output) + ")";
                    SimLog.Warning("[SimuLean] Runner: " + unavailable + "; studies run in the editor.");
                });
            }
        }

        /// <summary>The engine fingerprint the runner reports (null when it does not answer).</summary>
        private static string VersionOf(string exe, out string error)
        {
            error = null;
            int code = Execute(exe, "version", 30000, out string output);
            if (code != 0) { error = code == int.MinValue ? "cannot start" : Tail(output); return null; }
            try
            {
                foreach (string line in output.Split('\n'))
                    if (line.TrimStart().StartsWith("{") && MiniJson.Parse(line.Trim()) is Dictionary<string, object> o && o.TryGetValue("engine", out object e))
                        return e as string;
            }
            catch (FormatException) { }
            error = "no version answer";
            return null;
        }

        // a process to its end: exit code (int.MinValue = could not start), its output and error together
        private static int Execute(string file, string arguments, int timeoutMs, out string output)
        {
            var text = new System.Text.StringBuilder();
            try
            {
                using (var p = new Process { StartInfo = Info(file, arguments) })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "timed out"; return -1; }
                    p.WaitForExit();
                    lock (text) output = text.ToString();
                    return p.ExitCode;
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is FileNotFoundException || e is InvalidOperationException)
            {
                output = e.Message;
                return int.MinValue;
            }
        }

        private static ProcessStartInfo Info(string file, string arguments) => new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        private static string Tail(string text)
        {
            text = (text ?? "").Trim();
            return text.Length <= 300 ? text : "…" + text.Substring(text.Length - 300);
        }

        // ── one batch in the runner ──────────────────────────────────────────

        // ── the runner's session ─────────────────────────────────────────────

        /// <summary>
        /// The runner kept alive for the editor's session (<c>simulean serve</c>): .NET starts and compiles
        /// once, not per batch (an optimisation sends one per generation). Batches go one at a time over its standard input;
        /// each run comes back on its own line as soon as it ends and is decoded on a worker while the next ones run. It is
        /// closed when the editor reloads its scripts or quits, and started again when it has died.
        /// </summary>
        private sealed class Session
        {
            private readonly Process process;
            private readonly SemaphoreSlim oneBatch = new SemaphoreSlim(1, 1);
            private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
            private Job current;
            private int nextId;

            private sealed class Job
            {
                public string Id;
                public Action<int, int> Progress;
                public readonly List<Task<(int index, ResultRun run)>> Decoding = new List<Task<(int, ResultRun)>>();
                public readonly System.Text.StringBuilder Errors = new System.Text.StringBuilder();
                public string Last;
                public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            }

            public Session(string exe)
            {
                var info = Info(exe, "serve");
                info.RedirectStandardInput = true;
                info.StandardInputEncoding = new System.Text.UTF8Encoding(false);   // as the runner reads it (non-ASCII names)
                process = new Process { StartInfo = info, EnableRaisingEvents = true };
                process.OutputDataReceived += (s, e) => OnOutput(e.Data);
                process.ErrorDataReceived += (s, e) => { var job = current; OnErrorLine(e.Data, job?.Progress, job?.Errors ?? new System.Text.StringBuilder()); };
                process.Exited += (s, e) => { ready.Set(); current?.Done.Set(); };
                process.Start();
                process.StandardInput.AutoFlush = true;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!ready.Wait(30000) || process.HasExited) { Kill(); throw new InvalidOperationException("the runner did not start"); }
            }

            public bool Alive { get { try { return !process.HasExited; } catch { return false; } } }

            public void Kill()
            {
                try { if (!process.HasExited) { process.StandardInput.WriteLine("{\"kind\":\"exit\"}"); if (!process.WaitForExit(2000)) process.Kill(); } }
                catch { try { process.Kill(); } catch { } }
            }

            private void OnOutput(string line)
            {
                if (line == null) return;
                if (current == null && line.Contains("\"kind\":\"ready\"")) { ready.Set(); return; }   // {"format", "kind": "ready", …}
                var job = current;
                if (job == null) return;
                if (line.StartsWith("{\"kind\":\"run\"", StringComparison.Ordinal))
                    lock (job.Decoding) job.Decoding.Add(Task.Run(() => DecodeRun(line)));
                else if (line.StartsWith("{", StringComparison.Ordinal)) { job.Last = line; job.Done.Set(); }
                else lock (job.Errors) job.Errors.AppendLine(line);
            }

            public ResultSet Run(StudyBatch batch, CancellationToken token, Action<int, int> progress)
            {
                oneBatch.Wait(token);
                try
                {
                    var job = new Job { Id = (++nextId).ToString(System.Globalization.CultureInfo.InvariantCulture), Progress = progress };
                    current = job;
                    process.StandardInput.WriteLine(StudyBatch.Serialize(batch, job.Id));
                    using (token.Register(() => { try { process.StandardInput.WriteLine("{\"kind\":\"cancel\",\"id\":\"" + job.Id + "\"}"); } catch { } }))
                        job.Done.Wait();
                    current = null;
                    if (job.Last == null)
                    {
                        string message;
                        lock (job.Errors) message = job.Errors.ToString();
                        throw new InvalidOperationException("the runner stopped: " + Tail(message));
                    }
                    if (!(MiniJson.Parse(job.Last) is Dictionary<string, object> root))
                        throw new FormatException("the runner's answer is not a JSON object");
                    if ((root.TryGetValue("kind", out object k) ? k as string : null) == "error")
                        throw new InvalidOperationException(root.TryGetValue("message", out object m) ? m as string : "the runner failed");
                    ConfigJson.CheckEnvelope(root, "resultset");
                    if (!(root.TryGetValue(ResultSetJson.Key, out object s) && s is Dictionary<string, object> header))
                        throw new FormatException("the runner's answer has no resultSet");
                    var set = ResultSetJson.FromJson(header);
                    Task<(int index, ResultRun run)>[] tasks;
                    lock (job.Decoding) tasks = job.Decoding.ToArray();
                    Task.WaitAll(tasks);
                    foreach (var t in tasks.OrderBy(t => t.Result.index)) set.Runs.Add(t.Result.run);
                    return set;
                }
                finally
                {
                    current = null;
                    oneBatch.Release();
                }
            }
        }

        private static readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        private static bool closeHooked;

        private static Session SessionFor(string exe)
        {
            lock (gate)
            {
                if (!closeHooked)
                {
                    // the editor reloading its scripts or quitting closes the runners
                    AppDomain.CurrentDomain.DomainUnload += (s, e) => CloseSessions();
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => CloseSessions();
                    closeHooked = true;
                }
                if (sessions.TryGetValue(exe, out var session) && session.Alive) return session;
                session = new Session(exe);
                sessions[exe] = session;
                return session;
            }
        }

        /// <summary>Closes the runners kept for this editor session.</summary>
        public static void CloseSessions()
        {
            lock (gate)
            {
                foreach (var s in sessions.Values) s.Kill();
                sessions.Clear();
            }
        }

        private static ResultSet RunExternal(string exe, string jobs, StudyBatch batch, CancellationToken token, Action<int, int> progress)
        {
            var session = SessionFor(exe);
            try { return session.Run(batch, token, progress); }
            catch (InvalidOperationException) when (!session.Alive)
            {
                lock (gate) sessions.Remove(exe);   // it died: started again next time
                throw;
            }
        }

        private static (int, ResultRun) DecodeRun(string line)
        {
            var o = (Dictionary<string, object>)MiniJson.Parse(line);
            return (Convert.ToInt32(o["index"], System.Globalization.CultureInfo.InvariantCulture),
                    ResultSetJson.RunFromJson((Dictionary<string, object>)o["run"]));
        }

        // the runner's standard error: progress, the engine's log (into the editor's log, so an answer reports it)
        private static void OnErrorLine(string line, Action<int, int> progress, System.Text.StringBuilder errors)
        {
            if (line == null) return;
            if (line.StartsWith("runs ", StringComparison.Ordinal))
            {
                var parts = line.Substring(5).Split('/');
                if (parts.Length == 2 && int.TryParse(parts[0], out int d) && int.TryParse(parts[1], out int t)) progress?.Invoke(d, t);
                return;
            }
            if (line.StartsWith("[SimuLean:WARNING] ", StringComparison.Ordinal)) { SimLog.Warning(line.Substring(19)); return; }
            if (line.StartsWith("[SimuLean:ERROR] ", StringComparison.Ordinal)) { SimLog.Error(line.Substring(17)); return; }
            lock (errors) errors.AppendLine(line);
        }
    }
}
