using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Starts the EditMode tests from code (MCP <c>eval</c>, scripts) without ever blocking the editor:
    /// <list type="bullet">
    /// <item>Unity's Test Runner first asks "Scene(s) Have Been Modified" whenever an open scene has unsaved changes.
    /// That modal dialog stalls the editor's main thread, and with it every MCP command, until a human answers it.
    /// <see cref="Start"/> checks the open scenes first and does not start while one is modified. It returns the
    /// scenes instead: save them (if they are yours) or ask the user.</item>
    /// <item>The run is asynchronous. The call returns at once and the results go to <see cref="ResultsPath"/>
    /// (JSON: <c>status</c> running / completed / blocked, <c>full</c>, <c>startedUtc</c> ticks, counts, failures), read by
    /// <see cref="Status"/>.</item>
    /// <item>The commit gate (<c>.claude/hooks/test-gate.ps1</c>) accepts a <b>full</b> run (no filter) completed with no
    /// failure and no skipped test, whose working-tree fingerprint (<c>treeFp</c>, taken by the gate's own script when
    /// the run starts) still matches, instead of the batch run that needs the editor closed. A full run is also kept in
    /// <see cref="FullResultsPath"/>, which a later filtered or refused run never overwrites.</item>
    /// <item>Tests must leave the open scenes untouched (a full run left objects in the scene open in the
    /// editor, and dirtied it). The scenes open before each test are compared after it: an object left in one is a
    /// failure of that test, and it is removed; the whole run is checked again at its end (objects created a tick
    /// later). A scene the run left modified is reported too (<c>dirtied</c>: Unity cannot mark it clean again).</item>
    /// <item>The asset database is refreshed first: an edited script the unfocused editor has not imported yet is
    /// compiled before any test runs (the call then answers "blocked: compiling"; call again once it is done).</item>
    /// </list>
    /// Use from the Unity MCP: <c>return SimuLean.Unity.Editor.SimuLeanTestRunner.Start("LevelLuffingCraneViewTests");</c>,
    /// then poll <c>return SimuLean.Unity.Editor.SimuLeanTestRunner.Status();</c>. Also <i>Tools ▸ SimuLean ▸ Checks ▸
    /// Run EditMode Tests (Safe)</i>.
    /// </summary>
    public static class SimuLeanTestRunner
    {
        public const string ResultsPath = "Temp/simulean_test_results.json";
        public const string FullResultsPath = "Temp/simulean_test_results_full.json";

        private static TestRunnerApi api;
        private static Collector collector;

        /// <summary>
        /// The empty scene the agents and the tooling park the editor in (switching branches, between tasks). It never holds
        /// anyone's work, so its changes are discarded instead of asked about (<see cref="ModifiedScenes"/> leaves it out).
        /// </summary>
        public const string NeutralScene = "Assets/Scenes/Neutral.unity";

        /// <summary>The open scenes with unsaved changes (path, or "untitled") — the neutral scene is never one: its changes are
        /// discarded (reloaded from disk).</summary>
        public static List<string> ModifiedScenes()
        {
            DiscardNeutralChanges();
            var dirty = new List<string>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (s.isLoaded && s.isDirty) dirty.Add(string.IsNullOrEmpty(s.path) ? $"untitled '{s.name}'" : s.path);
            }
            return dirty;
        }

        /// <summary>
        /// Opens the neutral scene (alone) — e.g. before switching branches. Refuses, listing them, when another open scene
        /// has unsaved changes (never saved or discarded here). Returns "opened" or "blocked: …".
        /// </summary>
        public static string OpenNeutral()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "blocked: the editor is in play mode";
            var dirty = ModifiedScenes();
            if (dirty.Count > 0) return "blocked: unsaved changes in " + string.Join(", ", dirty);
            EditorSceneManager.OpenScene(NeutralScene, OpenSceneMode.Single);
            return "opened";
        }

        private static void DiscardNeutralChanges()
        {
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (!s.isLoaded || !s.isDirty || s.path != NeutralScene || EditorApplication.isPlayingOrWillChangePlaymode) continue;
                if (EditorSceneManager.sceneCount == 1) EditorSceneManager.OpenScene(NeutralScene, OpenSceneMode.Single);   // reload from disk
                else EditorSceneManager.CloseScene(s, true);
                return;
            }
        }

        /// <summary>
        /// Starts the EditMode tests whose full name contains <paramref name="filter"/> (empty = all). Returns
        /// "started", or "blocked: …" listing the modified scenes (nothing is started).
        /// </summary>
        public static string Start(string filter = "")
        {
            // "blocked" never writes the results files: the last run's results (and a passing full run) stay readable
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "blocked: the editor is in play mode";
            if (collector != null && collector.Running) return "already running";
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return "blocked: the editor is compiling / importing";
            // the unfocused editor does not import edited scripts: never test assemblies older than the sources
            AssetDatabase.Refresh();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "blocked: compiling the scripts changed on disk; call again once the editor is idle";
            var dirty = ModifiedScenes();
            if (dirty.Count > 0)
                return "blocked: open scenes have unsaved changes (the Test Runner would stop on a save dialog): " + string.Join(", ", dirty);

            if (api == null) api = ScriptableObject.CreateInstance<TestRunnerApi>();
            if (collector != null) api.UnregisterCallbacks(collector);
            collector = new Collector(filter ?? "", DateTime.UtcNow.Ticks, string.IsNullOrEmpty(filter) ? TreeFingerprint() : "");
            api.RegisterCallbacks(collector);
            Write("running", filter, null, collector);
            var f = new Filter { testMode = TestMode.EditMode };
            if (!string.IsNullOrEmpty(filter)) f.groupNames = new[] { Escape(filter) };
            api.Execute(new ExecutionSettings(f));
            return "started";
        }

        /// <summary>
        /// The commit gate's fingerprint of the working tree (<c>test-gate.ps1 treefp</c>: the files the EditMode verdict
        /// covers, including deletions and untracked ones), or "" when it cannot be taken (the gate then ignores the run).
        /// </summary>
        private static string TreeFingerprint()
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string script = Path.Combine(root, ".claude", "hooks", "test-gate.ps1");
                if (!File.Exists(script)) return "";
                var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" treefp")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000) || p.ExitCode != 0) return "";
                    return output.Trim();
                }
            }
            catch (Exception) { return ""; }
        }

        /// <summary>The last run's results file (JSON), or "none".</summary>
        public static string Status() => File.Exists(ResultsPath) ? File.ReadAllText(ResultsPath) : "none";

        [MenuItem("Tools/SimuLean/Checks/Run EditMode Tests (Safe)", priority = 300)]
        private static void RunAllFromMenu()
        {
            string r = Start();
            if (r != "started") EditorUtility.DisplayDialog("SimuLean tests", r, "OK");
        }

        // groupNames is a regex on the full test name: match the filter literally
        private static string Escape(string s) => System.Text.RegularExpressions.Regex.Escape(s);

        private static void Write(string status, string message, Collector c = null, Collector started = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"status\":\"").Append(status).Append("\",\"message\":\"").Append(Json(message)).Append('"');
            var run = c ?? started;
            if (run != null)
                sb.Append(",\"full\":").Append(run.Full ? "true" : "false").Append(",\"startedUtc\":").Append(run.StartedUtc)
                  .Append(",\"treeFp\":\"").Append(run.TreeFp).Append('"');
            if (c != null)
            {
                sb.Append(",\"passed\":").Append(c.Passed).Append(",\"failed\":").Append(c.Failed)
                  .Append(",\"skipped\":").Append(c.Skipped).Append(",\"failures\":[");
                for (int i = 0; i < c.Failures.Count; i++)
                    sb.Append(i > 0 ? "," : "").Append('"').Append(Json(c.Failures[i])).Append('"');
                sb.Append(']');
            }
            sb.Append('}');
            Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath));
            File.WriteAllText(ResultsPath, sb.ToString());
            if (run != null && run.Full) File.WriteAllText(FullResultsPath, sb.ToString());
        }

        private static string Json(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

        private class Collector : ICallbacks
        {
            /// <summary>After the run, the open scenes must keep the same root objects this long before they are checked.</summary>
            private const double SettleSeconds = 3.0;
            /// <summary>The longest the check waits for the scenes to come back and settle.</summary>
            private const double MaxWaitSeconds = 30.0;

            private readonly string filter;
            public readonly long StartedUtc;
            public readonly string TreeFp;
            public bool Full => filter.Length == 0;
            public bool Running = true;
            public int Passed, Failed, Skipped;
            public readonly List<string> Failures = new List<string>();

            private readonly UserScenes userScenes = UserScenes.Take();   // the editor's scenes before the Test Runner swaps them

            public Collector(string filter, long startedUtc, string treeFp) { this.filter = filter; StartedUtc = startedUtc; TreeFp = treeFp; }

            private OpenScenes beforeRun, beforeTest;

            public void RunStarted(ITestAdaptor testsToRun) { beforeRun = OpenScenes.Take(); }

            public void RunFinished(ITestResultAdaptor result)
            {
                string left = beforeRun?.Restore();
                if (left != null) { Failed++; Failures.Add("The EditMode run: " + left); }
                // Unity's Test Runner ran the tests in an untitled scene and puts the user's scenes back after this call:
                // they are checked once it has (completed only then)
                // The scenes come back in steps: Unity's default "Main Camera" appeared in the user's scene more than a second
                // after the paths matched, so the check waits until the open scenes' root objects
                // have stayed the same for SettleSeconds (at most MaxWaitSeconds in all).
                int ticks = 0;
                double since = EditorApplication.timeSinceStartup, stableSince = -1;
                string lastShape = null;
                EditorApplication.CallbackFunction check = null;
                check = () =>
                {
                    double now = EditorApplication.timeSinceStartup;
                    if (!UserScenes.Restored(userScenes) && ++ticks < 600 && now - since < MaxWaitSeconds) return;
                    string shape = UserScenes.Shape();
                    if (shape != lastShape) { lastShape = shape; stableSince = now; }
                    if (now - stableSince < SettleSeconds && now - since < MaxWaitSeconds) return;
                    EditorApplication.update -= check;
                    string changed = UserScenes.Check(userScenes);
                    if (changed != null) { Failed++; Failures.Add("The EditMode run: " + changed); }
                    Running = false;
                    Write("completed", filter, this);
                };
                EditorApplication.update += check;
            }

            public void TestStarted(ITestAdaptor test) { if (!test.HasChildren) beforeTest = OpenScenes.Take(); }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.HasChildren) return;
                string left = beforeTest?.Restore();
                beforeTest = null;
                if (left != null && result.TestStatus == TestStatus.Passed)
                {
                    Failed++; Failures.Add(result.FullName + ": " + left);
                    return;
                }
                switch (result.TestStatus)
                {
                    case TestStatus.Passed: Passed++; break;
                    case TestStatus.Failed: Failed++; Failures.Add(result.FullName + ": " + result.Message + (left != null ? " — and " + left : "")); break;
                    default: Skipped++; break;
                }
            }
        }

        /// <summary>
        /// The scenes open in the editor when a run starts (none modified: <see cref="Start"/> refuses), by path, with the names
        /// of their root objects. Unity's Test Runner reopens them after the run, and they came back with objects that are
        /// not in the files — a default "Main Camera" in the empty neutral scene, not even marked modified, so the next
        /// save kept it. A scene that comes back different is loaded again from its file; one that is
        /// modified is reported, never discarded.
        /// </summary>
        private sealed class UserScenes
        {
            private readonly List<(string path, bool loaded, List<string> roots)> scenes = new List<(string, bool, List<string>)>();
            private string active;

            public static UserScenes Take()
            {
                var u = new UserScenes { active = EditorSceneManager.GetActiveScene().path };
                for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                {
                    var s = EditorSceneManager.GetSceneAt(i);
                    u.scenes.Add((s.path, s.isLoaded, s.isLoaded ? RootNames(s) : null));
                }
                return u;
            }

            private static List<string> RootNames(UnityEngine.SceneManagement.Scene s)
            {
                var names = new List<string>();
                foreach (var r in s.GetRootGameObjects()) names.Add(r.name);
                names.Sort(StringComparer.Ordinal);
                return names;
            }

            /// <summary>The open scenes and the names of their root objects, as one text (to see when they stop changing).</summary>
            public static string Shape()
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                {
                    var s = EditorSceneManager.GetSceneAt(i);
                    sb.Append(s.path).Append(s.isLoaded ? ":" + string.Join(",", RootNames(s)) : ":-").Append('|');
                }
                return sb.ToString();
            }

            /// <summary>True once the editor shows the same scenes (by path) as before the run.</summary>
            public static bool Restored(UserScenes u)
            {
                if (u == null) return true;
                if (EditorSceneManager.sceneCount != u.scenes.Count) return false;
                for (int i = 0; i < u.scenes.Count; i++) if (EditorSceneManager.GetSceneAt(i).path != u.scenes[i].path) return false;
                return true;
            }

            /// <summary>Reloads the scenes that came back with other root objects; returns the modified ones (or null).</summary>
            public static string Check(UserScenes u)
            {
                if (u == null || !Restored(u) || EditorApplication.isPlayingOrWillChangePlaymode) return null;
                bool reload = false;
                var modified = new List<string>();
                for (int i = 0; i < u.scenes.Count; i++)
                {
                    var (path, loaded, roots) = u.scenes[i];
                    var s = EditorSceneManager.GetSceneAt(i);
                    if (!loaded || !s.isLoaded || string.IsNullOrEmpty(path)) continue;
                    var now = RootNames(s);
                    if (now.Count == roots.Count && now.TrueForAll(n => roots.Contains(n))) continue;
                    if (s.isDirty && path != NeutralScene) modified.Add($"{path} (now {string.Join(", ", now)})");
                    else reload = true;
                }
                if (reload && modified.Count == 0)
                {
                    Debug.Log("[SimuLean] The Test Runner put the open scenes back with objects that are not in their files: reloaded from disk.");
                    for (int i = 0; i < u.scenes.Count; i++)
                    {
                        var (path, loaded, _) = u.scenes[i];
                        if (string.IsNullOrEmpty(path)) continue;
                        var s = EditorSceneManager.OpenScene(path, i == 0 ? OpenSceneMode.Single
                                                                    : loaded ? OpenSceneMode.Additive : OpenSceneMode.AdditiveWithoutLoading);
                        if (s.path == u.active) EditorSceneManager.SetActiveScene(s);
                    }
                }
                return modified.Count > 0 ? "the open scenes came back changed: " + string.Join("; ", modified) : null;
            }
        }

        /// <summary>The root objects of the open scenes, to find (and remove) what a test left in them.</summary>
        private sealed class OpenScenes
        {
            private readonly List<(UnityEngine.SceneManagement.Scene scene, HashSet<GameObject> roots, bool dirty)> scenes =
                new List<(UnityEngine.SceneManagement.Scene, HashSet<GameObject>, bool)>();

            public static OpenScenes Take()
            {
                var o = new OpenScenes();
                for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                {
                    var s = EditorSceneManager.GetSceneAt(i);
                    if (s.isLoaded) o.scenes.Add((s, new HashSet<GameObject>(s.GetRootGameObjects()), s.isDirty));
                }
                return o;
            }

            /// <summary>Removes the root objects added since the snapshot to the scenes still open; returns what was left
            /// ("left … in …"), or null when nothing was.</summary>
            public string Restore()
            {
                var report = new List<string>();
                foreach (var (scene, roots, dirty) in scenes)
                {
                    if (!scene.IsValid() || !scene.isLoaded) continue;
                    var added = new List<GameObject>();
                    foreach (var r in scene.GetRootGameObjects()) if (!roots.Contains(r)) added.Add(r);
                    if (added.Count > 0)
                    {
                        var names = new List<string>();
                        foreach (var go in added) { names.Add(Describe(go)); UnityEngine.Object.DestroyImmediate(go); }
                        report.Add($"left {string.Join(", ", names)} in the open scene '{(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path)}' (removed)");
                    }
                }
                return report.Count > 0 ? string.Join("; ", report) : null;
            }

            // "name [components]" of an object and its children (the organiser groups hold the element left behind)
            private static string Describe(GameObject go)
            {
                var parts = new List<string>();
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                {
                    var types = new List<string>();
                    foreach (var c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) types.Add(c.GetType().Name);
                    parts.Add(types.Count > 0 ? $"'{t.name}' ({string.Join(", ", types)})" : $"'{t.name}'");
                }
                return string.Join(" > ", parts);
            }
        }
    }
}
