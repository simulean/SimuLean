using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The answer of every <c>simulean_*</c> command: the command's own fields plus <c>errors</c> and
    /// <c>warnings</c> — what the engine (<see cref="SimLog"/>, bridged to the console), the extractor and the editor logged
    /// while the command ran, which before reached only the Unity console. A command that fails throws as before, its
    /// message followed by the warnings logged until then.
    /// </summary>
    internal static class AgentReply
    {
        /// <summary>At most this many messages of each kind in an answer (the count of the rest is given).</summary>
        private const int MaxMessages = 20;

        /// <summary>Runs <paramref name="body"/> capturing the log; its answer (an object) with <c>errors</c> / <c>warnings</c>
        /// added to any the body reports itself.</summary>
        public static object Of(Func<object> body)
        {
            using (var log = new LogCapture())
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
        /// The warnings and errors logged (any thread: headless runs log from workers) while it is alive; messages only,
        /// without stack traces, each once. Used by the command answers and the background run.
        /// </summary>
        public sealed class LogCapture : IDisposable
        {
            private readonly List<string> errors = new List<string>(), warnings = new List<string>();
            private readonly object gate = new object();
            private bool disposed;

            public LogCapture() { Application.logMessageReceivedThreaded += OnLog; }

            public List<string> Errors { get { lock (gate) return new List<string>(errors); } }
            public List<string> Warnings { get { lock (gate) return new List<string>(warnings); } }

            private void OnLog(string message, string stackTrace, LogType type)
            {
                if (type == LogType.Log || string.IsNullOrWhiteSpace(message)) return;
                string m = message.Trim();
                lock (gate)
                {
                    var list = type == LogType.Warning ? warnings : errors;
                    if (!list.Contains(m)) list.Add(m);
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                Application.logMessageReceivedThreaded -= OnLog;
            }
        }
    }
}
