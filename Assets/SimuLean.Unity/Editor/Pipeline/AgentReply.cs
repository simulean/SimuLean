using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The answer of every <c>simulean_*</c> command: the command's own fields plus <c>errors</c> and
    /// <c>warnings</c> — what the engine (<see cref="SimLog"/>, bridged to the console), the extractor and the editor logged
    /// while the command ran (otherwise they reach only the Unity console). A command that fails throws, its
    /// message followed by the warnings logged until then.
    /// </summary>
    internal static class AgentReply
    {
        /// <summary>At most this many messages of each kind in an answer (the count of the rest is given).</summary>
        private const int MaxMessages = 20;

        /// <summary>Runs <paramref name="body"/> capturing the log; its answer (an object) with <c>errors</c> / <c>warnings</c>
        /// added to any the body reports itself.</summary>
        public static object Of(Func<object> body) => Run(body);

        /// <summary><see cref="Of"/> typed: for a command that reads the answer of another (<c>simulean_apply</c>'s operations,
        /// the status waits) without a JSON round-trip.</summary>
        public static JObject Run(Func<object> body)
        {
            using (var log = new LogCapture())
            using (log.Enter())
            {
                object result;
                try { result = body(); }
                catch (Exception e)
                {
                    var inner = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                    var warnings = log.Warnings;
                    if (warnings.Count == 0) throw;
                    throw new ArgumentException(inner.Message + " Warnings logged: " + string.Join(" | ", warnings), inner);
                }
                return With(result, log.Errors, log.Warnings);
            }
        }

        /// <summary>The answer <paramref name="result"/> (an object; a list becomes <c>{result: […]}</c>) with
        /// <c>errors</c> and <c>warnings</c> (existing arrays of the same name are merged).</summary>
        public static JObject With(object result, IEnumerable<string> errors, IEnumerable<string> warnings)
        {
            var token = result == null ? new JObject() : JToken.FromObject(result);
            var o = token as JObject ?? new JObject { ["result"] = token };
            Merge(o, "errors", errors);
            Merge(o, "warnings", warnings);
            return o;
        }

        private static void Merge(JObject o, string name, IEnumerable<string> extra)
        {
            var list = new List<string>();
            if (o[name] is JArray a) list.AddRange(a.Select(x => (string)x));
            foreach (string m in extra ?? Enumerable.Empty<string>()) if (!string.IsNullOrEmpty(m) && !list.Contains(m)) list.Add(m);
            o[name] = new JArray(Cap(list).Cast<object>().ToArray());
        }

        private static List<string> Cap(List<string> list)
        {
            if (list.Count <= MaxMessages) return list;
            var kept = list.Take(MaxMessages).ToList();
            kept.Add($"… and {list.Count - MaxMessages} more (Unity console)");
            return kept;
        }

        /// <summary>
        /// The warnings and errors logged while it is alive and attributed to it; messages only, without stack traces, each
        /// once. Used by the command answers (<see cref="Of"/>) and the background jobs (run, optimisation).
        /// <para>Attribution (without it a background run's warnings would appear in the answer of an unrelated command
        /// polled meanwhile, and the run's answer would collect the main thread's logs): a capture is <see cref="Enter">entered</see>
        /// as the logging scope of its work, an <see cref="AsyncLocal{T}"/> that flows into the tasks and parallel loops that
        /// work starts (headless runs log from workers). A message logged inside a scope goes to that capture and the captures
        /// it was entered in; one logged outside any scope (a thread that does not carry it, e.g. an external runner's output)
        /// goes to every command capture alive, and to the background jobs only from a worker thread — never a main-thread
        /// message logged between commands (a deferred editor callback). The object a message was logged with
        /// (<c>Debug.LogWarning(message, context)</c>) is named in front of it when the message does not name it.</para>
        /// </summary>
        public sealed class LogCapture : IDisposable
        {
            private static readonly System.Threading.AsyncLocal<LogCapture> current = new System.Threading.AsyncLocal<LogCapture>();
            private static readonly List<LogCapture> alive = new List<LogCapture>();
            private static readonly object aliveGate = new object();
            private static bool hooked;

            private readonly List<string> errors = new List<string>(), warnings = new List<string>();
            private readonly object gate = new object();
            private readonly bool job;
            private LogCapture parent;
            private bool disposed;

            /// <summary>A capture; <paramref name="job"/> = a background job's, alive across commands.</summary>
            public LogCapture(bool job = false)
            {
                this.job = job;
                NamingLogHandler.Install();
                lock (aliveGate)
                {
                    alive.Add(this);
                    if (!hooked) { Application.logMessageReceivedThreaded += Dispatch; hooked = true; }
                }
            }

            public List<string> Errors { get { lock (gate) return new List<string>(errors); } }
            public List<string> Warnings { get { lock (gate) return new List<string>(warnings); } }

            /// <summary>Makes this capture the logging scope of the code that follows on this thread and of the tasks it
            /// starts, until disposed. A command's capture is entered inside the scope it runs in (a nested command's
            /// messages reach the outer one too); a job's capture (<paramref name="detached"/>) only in its own.</summary>
            public IDisposable Enter(bool detached = false)
            {
                var previous = current.Value;
                parent = detached ? null : previous;
                current.Value = this;
                return new Scope(previous);
            }

            private sealed class Scope : IDisposable
            {
                private readonly LogCapture previous;
                private bool done;
                public Scope(LogCapture previous) { this.previous = previous; }
                public void Dispose() { if (done) return; done = true; current.Value = previous; }
            }

            private static void Dispatch(string message, string stackTrace, LogType type)
            {
                if (type == LogType.Log || string.IsNullOrWhiteSpace(message)) return;
                string m = NamingLogHandler.Named(message.Trim());
                var scope = current.Value;
                if (scope != null)
                {
                    for (var c = scope; c != null; c = c.parent) c.Add(m, type);
                    return;
                }
                bool worker = !NamingLogHandler.OnMainThread;
                List<LogCapture> targets;
                lock (aliveGate) targets = alive.Where(c => !c.job || worker).ToList();
                foreach (var c in targets) c.Add(m, type);
            }

            private void Add(string m, LogType type)
            {
                lock (gate)
                {
                    if (disposed) return;
                    var list = type == LogType.Warning ? warnings : errors;
                    if (list.Contains(m)) return;
                    // a study logs its deadlock / no-progress warning once per replication, differing only in its numbers
                    // (time, item counts): 17 near-identical lines buried the answer — keep the first, count the others;
                    // a model's own warnings with their time (t=…) likewise, worded as StudySpec.ModelWarningNotes puts
                    // them in a study's answer, so that the two copies are one
                    if (type == LogType.Warning && (m.StartsWith("[SimuLean] Deadlock at t", StringComparison.Ordinal)
                                                    || m.StartsWith("[SimuLean] No progress at t", StringComparison.Ordinal)
                                                    || m.StartsWith("[SimuLean] t=", StringComparison.Ordinal)))
                    {
                        string key = System.Text.RegularExpressions.Regex.Replace(m, @"\d+(\.\d+)?", "#");
                        if (repeats.TryGetValue(key, out var r))
                        {
                            r.more++;
                            list[r.index] = r.first + $" (and in {r.more} more run(s) of this answer, at other times)";
                            return;
                        }
                        repeats[key] = new Repeat { index = list.Count, first = m };
                    }
                    list.Add(m);
                }
            }

            private sealed class Repeat { public int index, more; public string first; }
            private readonly Dictionary<string, Repeat> repeats = new Dictionary<string, Repeat>();

            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                }
                lock (aliveGate) alive.Remove(this);
            }
        }

        /// <summary>
        /// Passes every log call on to Unity's handler, keeping the object it was logged with for the moment the message is
        /// dispatched (on the same thread, inside the call), so that <see cref="LogCapture"/> can name it. Installed once,
        /// read-only: the console gets the same messages.
        /// </summary>
        private sealed class NamingLogHandler : ILogHandler
        {
            [ThreadStatic] private static Object context;
            [ThreadStatic] private static bool mainThreadKnown, mainThread;
            private readonly ILogHandler inner;

            private NamingLogHandler(ILogHandler inner) { this.inner = inner; }

            public static void Install()
            {
                // the commands and jobs are created on the main thread, where the logger may be replaced
                if (!UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread()) return;
                mainThreadKnown = true; mainThread = true;
                var logger = Debug.unityLogger;
                if (!(logger.logHandler is NamingLogHandler)) logger.logHandler = new NamingLogHandler(logger.logHandler);
            }

            public static bool OnMainThread
            {
                get
                {
                    if (!mainThreadKnown)
                    {
                        mainThread = UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread();
                        mainThreadKnown = true;
                    }
                    return mainThread;
                }
            }

            /// <summary>The message with the name of the object it was logged with in front, when it does not name it
            /// (main thread only: a Unity object's name cannot be read elsewhere).</summary>
            public static string Named(string message)
            {
                var c = context;
                if (c == null || !OnMainThread) return message;
                string name;
                try { name = c.name; } catch { return message; }
                if (string.IsNullOrEmpty(name) || message.Contains(name)) return message;
                return $"[{name}] {message}";
            }

            public void LogFormat(LogType logType, Object ctx, string format, params object[] args)
            {
                var previous = context;
                context = ctx;
                try { inner.LogFormat(logType, ctx, format, args); }
                finally { context = previous; }
            }

            public void LogException(Exception exception, Object ctx)
            {
                var previous = context;
                context = ctx;
                try { inner.LogException(exception, ctx); }
                finally { context = previous; }
            }
        }
    }
}
