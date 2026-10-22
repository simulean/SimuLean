using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The wait of the long-job status commands: a <c>_status</c> call with
    /// <c>wait</c> &gt; 0 answers as soon as the job is no longer going on, or when the wait is over — instead of the agent
    /// polling (sleep-and-poll through <c>eval</c> cost half of a long agent session). The status is read on the main thread at
    /// each editor tick (every <see cref="Interval"/>); the command returns a task, which the pipeline server awaits off the
    /// main thread, so the editor (and the job) keeps going meanwhile. A domain reload ends the wait with the status then.
    /// </summary>
    internal static class JobWait
    {
        /// <summary>The longest wait of one call (under the MCP client's timeout).</summary>
        public const double MaxSeconds = 55;
        private const double Interval = 0.25;

        /// <summary>The statuses of a job that is still going on.</summary>
        private static readonly string[] Going = { "running", "starting", "approaching", "recording" };

        /// <summary>True when the answer <paramref name="status"/> (a JSON object with a <c>status</c> field) is of a job
        /// still going on.</summary>
        public static bool IsGoing(JToken status) =>
            status is JObject o && o["status"] is JValue v && Array.IndexOf(Going, (string)v) >= 0;

        /// <summary>The answer of <paramref name="status"/> once the job is no longer going on, or after
        /// <paramref name="seconds"/> (at most <see cref="MaxSeconds"/>; 0 = at once).</summary>
        public static Task<object> Until(Func<JToken> status, double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) throw new ArgumentException($"wait: {seconds} is not 0 or more seconds.");
            JToken first = status();
            if (seconds <= 0 || !IsGoing(first)) return Task.FromResult<object>(first);

            var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            double end = EditorApplication.timeSinceStartup + Math.Min(seconds, MaxSeconds), next = 0;
            EditorApplication.CallbackFunction tick = null;
            AssemblyReloadEvents.AssemblyReloadCallback reload = null;

            void Finish(JToken answer)
            {
                EditorApplication.update -= tick;
                AssemblyReloadEvents.beforeAssemblyReload -= reload;
                done.TrySetResult(answer);
            }
            void Check(bool last)
            {
                try
                {
                    JToken now = status();
                    if (last || !IsGoing(now) || EditorApplication.timeSinceStartup >= end) Finish(now);
                }
                catch (Exception e)
                {
                    EditorApplication.update -= tick;
                    AssemblyReloadEvents.beforeAssemblyReload -= reload;
                    done.TrySetException(e);
                }
            }
            tick = () =>
            {
                if (EditorApplication.timeSinceStartup < next) return;
                next = EditorApplication.timeSinceStartup + Interval;
                Check(false);
            };
            reload = () => Check(true);
            EditorApplication.update += tick;
            AssemblyReloadEvents.beforeAssemblyReload += reload;
            return done.Task;
        }

        /// <summary>An answer as JSON (an object, a JSON text, or a plain text as <c>{result}</c>).</summary>
        public static JToken Json(object answer)
        {
            if (answer is JToken t) return t;
            if (answer is string s)
            {
                try { return JToken.Parse(s); }
                catch (Newtonsoft.Json.JsonException) { return new JObject { ["result"] = s }; }
            }
            return answer == null ? new JObject() : JToken.FromObject(answer);
        }
    }
}
