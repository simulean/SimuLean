using System.Linq;
using SimuLean;
using SimuLean.Unity;
using UnityEditor;
using SimuLean.Results;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="UnitySimClock"/>.
    /// Shows a live simulation-time readout, a separate "Run Headless" button
    /// (<see cref="HeadlessSceneRun"/>) with its export and the Results tab.
    /// </summary>
    [CustomEditor(typeof(UnitySimClock))]
    public class UnitySimClockEditor : UnityEditor.Editor
    {
        private double _lastRepaint;
        private static bool _showRuntimeOptions;

        // Last headless run (static: survives re-selecting the clock; lost on domain reload).
        private static HeadlessSceneRun _headlessRun;
        private static Vector2 _headlessScroll;
        private bool _headlessWasRunning;

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            // Repaint at ~10 Hz during play mode (or a headless run) so buttons reflect current sim state.
            bool headless = _headlessRun?.IsRunning ?? false;
            bool headlessJustEnded = _headlessWasRunning && !headless;   // one more repaint to show the results
            _headlessWasRunning = headless;
            if (headlessJustEnded) { Repaint(); return; }
            if (!Application.isPlaying && !headless) return;
            if (EditorApplication.timeSinceStartup - _lastRepaint < 0.1) return;
            _lastRepaint = EditorApplication.timeSinceStartup;
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var clock = (UnitySimClock)target;
            SimuLeanGUI.ToolHeader(clock, Application.isPlaying ? (clock.SimPaused ? "PAUSED" : clock.SimOn ? "RUNNING" : "READY") : null);

            // ── Configuration ─────────────────────────────────────────────────
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("maxTime"),
                new GUIContent($"Max Simulation Time ({SimuLeanGUI.TimeUnit})", "Simulated time duration of one scenario run, in model time units (Seconds per Time Unit below)."));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("timeScale"),
                new GUIContent("Time Scale", "Simulation speed multiplier relative to Unity time."));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("startDateTime"),
                new GUIContent("Start Date/Time", "Calendar date of simulation time 0 (e.g. 2026-01-01 06:00). Empty = 2000-01-01 00:00."));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("timeUnitSeconds"),
                new GUIContent("Seconds per Time Unit", "1 = the simulation clock counts seconds."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("holidays"),
                new GUIContent("Holidays", "The model's holidays (dates): every shift is off on them (one working calendar)."), true);

            // ── End condition ──────────────────────────────────────
            EditorGUILayout.PropertyField(serializedObject.FindProperty("stopAt"), new GUIContent("Stop At", "End condition: the elements whose received items stop the run — sinks, or the queues that hold the finished product (empty = off)."), true);
            if (serializedObject.FindProperty("stopAt").arraySize > 0)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("stopAtCount"), new GUIContent("Stop At Count", "Items those elements must receive between them."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("stopWhenEmpty"), new GUIContent("Stop When Empty", "End condition: nothing left to do — every item out, at rest in a buffer without outputs, or surplus nothing will take; or a deadlock / stall."));

            // ── Results (the fields were serialized but never drawn: Record Timeline could not be switched on) ──
            // (the "Results" heading is the field's [Header]; review2 #42: it was drawn twice)
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exportOnFinish"), new GUIContent("Export On Finish",
                "Formats written when a Play run finishes (Nothing = no file; the SimuLean window's Results tab still shows the run)."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("seriesInterval"), new GUIContent("Series Interval",
                "Time series (content, throughput, states per interval) of a Play run and of Run Headless — the Results tab's Over time. 0 = none."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("recordTimeline"), new GUIContent("Record Timeline",
                "Keep every state interval of elements and resources, and the items' path through the stations: the Results tab's Timeline (Gantt) and a dashboard's live Gantt. The results grow with every state change."));

            // ── Optional runtime features (defaults = legacy behaviour) ───────
            _showRuntimeOptions = EditorGUILayout.Foldout(_showRuntimeOptions, "Optional runtime features", true);
            if (_showRuntimeOptions)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("autoStartOnPlay"),
                    new GUIContent("Auto Start On Play", "Start the simulation when entering Play mode (default on; off for scenes driven by an Experimenter / Optimizer)."));
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("frameBudgetMs"),
                    new GUIContent("Frame Budget (ms)", "0 = legacy loop (default). > 0 = paced mode for large speed-ups: the clock advances in slices within this wall-clock budget per frame and drops lag instead of freezing the view."));
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("escapeQuits"),
                    new GUIContent("Escape Quits", "Esc quits the application (legacy default on)."));
                EditorGUI.indentLevel--;
            }

            DrawSeedSection(clock);

            SimCalendar calendar = clock.CreateCalendar();
            if (!calendar.IsDefault)
                EditorGUILayout.LabelField("Ends at", calendar.Format(clock.MaxTime, "yyyy-MM-dd HH:mm:ss"), EditorStyles.miniLabel);

            // ── Simulation Controls ───────────────────────────────────────────
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Simulation Controls", EditorStyles.boldLabel);

            // Always visible (also before Play). In Play mode the property re-anchors the paced loop.
            var fastContent = new GUIContent("Run As Fast As Possible",
                "Maximum speed: ignore Time Scale and advance as far as the frame budget allows (paced mode). The scene and its views keep running.");
            if (Application.isPlaying)
            {
                serializedObject.ApplyModifiedProperties();
                bool fast = EditorGUILayout.Toggle(fastContent, clock.RunAsFastAsPossible);
                if (fast != clock.RunAsFastAsPossible) clock.RunAsFastAsPossible = fast;
                if (clock.Paced)
                    EditorGUILayout.LabelField("Effective speed", $"{clock.EffectiveSpeed:F0} sim-units / s", EditorStyles.miniLabel);
                serializedObject.Update();
            }
            else
                EditorGUILayout.PropertyField(serializedObject.FindProperty("runAsFastAsPossible"), fastContent);
            serializedObject.ApplyModifiedProperties();

            if (Application.isPlaying)
            {
                // Auto-detect any IExperimenter in the scene ──────────────────
                IExperimenter experimenter = FindExperimenterInScene();

                if (experimenter != null)
                {
                    EditorGUILayout.HelpBox(
                        "Experimenter detected in scene.\n" +
                        "Use \u25b6\u2009Run Experiments to execute all defined scenarios.",
                        MessageType.Info);
                    EditorGUILayout.Space(2);

                    EditorGUILayout.BeginHorizontal();
                    GUI.enabled = !experimenter.IsRunning;
                    if (GUILayout.Button("\u25b6  Run Experiments", GUILayout.Height(36)))
                        experimenter.StartExperiments();
                    GUI.enabled = experimenter.IsRunning;
                    if (GUILayout.Button("\u23f9  Cancel", GUILayout.Height(36), GUILayout.Width(90)))
                        experimenter.CancelExperiments();
                    GUI.enabled = true;
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField("\u2500\u2500\u2500 or run a single simulation independently \u2500\u2500\u2500",
                        EditorStyles.centeredGreyMiniLabel);
                    EditorGUILayout.Space(2);
                }

                // Single-run controls ─────────────────────────────────────────
                bool isRunning = clock.SimOn;
                bool isPaused  = clock.SimPaused;

                EditorGUILayout.BeginHorizontal();
                GUI.enabled = !isRunning || isPaused;
                string runLabel = isPaused ? "\u25b6  Resume" : "\u25b6  Run Simulation";
                if (GUILayout.Button(runLabel, GUILayout.Height(36)))
                {
                    if (isPaused) clock.ResumeSim();
                    else          clock.RunSimulation();
                }
                GUI.enabled = isRunning && !isPaused;
                if (GUILayout.Button("\u23f8  Pause", GUILayout.Height(36), GUILayout.Width(90)))
                    clock.PauseSim();
                GUI.enabled = true;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(2);
                GUI.enabled = clock.SimStarted;
                if (GUILayout.Button("\u23f9  Stop & Reset", GUILayout.Height(28)))
                    clock.StopSimulation();
                GUI.enabled = true;
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Click the button below to enter Play mode and start the simulation automatically.\n" +
                    "Or press Play manually, then use the runtime controls once in Play mode.",
                    MessageType.Info);
                EditorGUILayout.Space(4);

                GUI.backgroundColor = new Color(0.35f, 0.90f, 0.45f);
                if (GUILayout.Button("\u25b6  Run Simulation", GUILayout.Height(40)))
                {
                    // Set a serialised flag so Start() auto-calls RunSimulation() when
                    // Unity finishes entering Play mode.
                    var sp = serializedObject.FindProperty("_startOnPlay");
                    if (sp != null) { sp.boolValue = true; serializedObject.ApplyModifiedProperties(); }
                    EditorApplication.isPlaying = true;
                }
                GUI.backgroundColor = Color.white;
            }

            DrawHeadlessRun(clock);

            // ── Live simulation time ──────────────────────────────────────────
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Simulation Time", EditorStyles.boldLabel);

            if (Application.isPlaying)
            {
                double simTime = clock.SmoothSimTime;
                double maxTime = clock.MaxTime;
                float  progress = maxTime > 0 ? Mathf.Clamp01((float)(simTime / maxTime)) : 0f;

                GUIStyle valStyle = new GUIStyle(EditorStyles.boldLabel)
                    { normal = { textColor = new Color(0.25f, 0.75f, 1f) } };

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Current Sim Time", GUILayout.Width(EditorGUIUtility.labelWidth));
                EditorGUILayout.LabelField(FormatSimTime(simTime), valStyle);
                EditorGUILayout.EndHorizontal();

                if (!clock.clock.Context.Calendar.IsDefault)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Calendar", GUILayout.Width(EditorGUIUtility.labelWidth));
                    EditorGUILayout.LabelField(clock.clock.Context.Calendar.Format(simTime, "ddd yyyy-MM-dd HH:mm:ss"), valStyle);
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Unity Time", GUILayout.Width(EditorGUIUtility.labelWidth));
                EditorGUILayout.LabelField(FormatSimTime(Time.time), valStyle);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Max Time", GUILayout.Width(EditorGUIUtility.labelWidth));
                EditorGUILayout.LabelField(FormatSimTime(maxTime));
                EditorGUILayout.EndHorizontal();

                Rect rect = GUILayoutUtility.GetRect(18, 16, GUILayout.ExpandWidth(true));
                rect = EditorGUI.IndentedRect(rect);
                EditorGUI.ProgressBar(rect, progress, $"{progress * 100f:F1} %");
            }
            else
            {
                EditorGUILayout.HelpBox("Simulation time is displayed during Play mode.", MessageType.Info);
            }
        }

        // ── Headless run ──────────────────────────────────────────────────────

        /// <summary>
        /// "Run Headless": extracts the scene and runs it to the end without views on a worker thread
        /// (<see cref="HeadlessSceneRun"/>); the scene is untouched and only the result table is shown.
        /// </summary>
        private static void DrawHeadlessRun(UnitySimClock clock)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Headless Run (results only)", EditorStyles.boldLabel);

            var run = _headlessRun;
            bool running = run != null && run.IsRunning;

            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !running;
            if (GUILayout.Button(new GUIContent("⚡  Run Headless",
                    "Extract the scene and run it to Max Simulation Time without views, on a worker thread " +
                    "(warm-up, seed and calendar from this clock). The scene is not modified; only the " +
                    "per-element statistics are shown."), GUILayout.Height(28)))
            {
                try { _headlessRun = HeadlessSceneRun.Start(clock.SeriesInterval, clock.RecordTimeline); _headlessScroll = Vector2.zero; }
                catch (System.Exception ex) { Debug.LogError($"[UnitySimClock] Headless run could not start: {ex}"); }
            }
            GUI.enabled = running;
            if (GUILayout.Button("⏹  Cancel", GUILayout.Height(28), GUILayout.Width(90)))
                run.Cancel();
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (run == null) return;

            Rect rect = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(18, 16, GUILayout.ExpandWidth(true)));
            string status = run.State switch
            {
                HeadlessSceneRun.RunState.Running   => $"{run.Progress * 100:F1} %",
                HeadlessSceneRun.RunState.Finished  => "Finished",
                HeadlessSceneRun.RunState.Cancelled => "Cancelled",
                _                                   => "Failed (see Console)",
            };
            EditorGUI.ProgressBar(rect, (float)run.Progress, status);
            EditorGUILayout.LabelField(
                $"{run.ElementCount} elements · {FormatSimTime(run.SimTime)} simulated in {run.WallSeconds:F2} s real " +
                $"(×{run.EffectiveSpeed:N0}) · seed {(run.Seed.HasValue ? run.Seed.Value.ToString() : "random")}",
                EditorStyles.miniLabel);

            var results = run.Results;
            if (run.State != HeadlessSceneRun.RunState.Finished || results == null) return;

            // Compact view of the elements: in, out, average content, average stay, states (all statistics: CSV).
            var head = EditorStyles.miniBoldLabel;
            var cell = EditorStyles.miniLabel;
            _headlessScroll = EditorGUILayout.BeginScrollView(_headlessScroll, GUILayout.MaxHeight(220));
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Element", head, GUILayout.Width(110));
            GUILayout.Label("In", head, GUILayout.Width(50));
            GUILayout.Label("Out", head, GUILayout.Width(50));
            GUILayout.Label("Avg content", head, GUILayout.Width(70));
            GUILayout.Label("Avg stay", head, GUILayout.Width(60));
            GUILayout.Label("States", head);
            EditorGUILayout.EndHorizontal();
            foreach (var o in results.Of(ResultScope.Element).OrderBy(o => o.Name, System.StringComparer.Ordinal))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent(o.Name, o.Type), cell, GUILayout.Width(110));
                GUILayout.Label($"{o[ResultMetrics.Input]:0}", cell, GUILayout.Width(50));
                GUILayout.Label($"{o[ResultMetrics.Output]:0}", cell, GUILayout.Width(50));
                GUILayout.Label($"{o[ResultMetrics.ContentAvg]:F2}", cell, GUILayout.Width(70));
                GUILayout.Label($"{o[ResultMetrics.StayAvg]:F1}", cell, GUILayout.Width(60));
                GUILayout.Label(string.Join(" ", o.Metrics.Where(m => ResultMetrics.IsState(m.Key) && m.Value > 0)
                    .OrderByDescending(m => m.Value).Select(m => $"{m.Key.Substring(ResultMetrics.StatePrefix.Length)} {m.Value * 100:F1}%")), cell);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Copy CSV", "Every statistic of the run, one per row (scope, name, type, group, metric, value).")))
                EditorGUIUtility.systemCopyBuffer = results.ToLongTable().ToCsv();
            var settings = SimuLeanProjectSettings.Load();
            if (GUILayout.Button(new GUIContent("Export", $"Writes the run ({settings.defaultFormats}) to the results folder ({settings.resultsFolder}) — " +
                                                          "formats and folder: SimuLean window ▸ Settings.")) && run.ResultSet != null)
            {
                foreach (string path in ResultsOutput.Export(run.ResultSet, settings.defaultFormats, ResultsOutput.ProjectRoot))
                    Debug.Log($"[UnitySimClock] Results: {path}");
            }
            GUI.enabled = run.ResultSet != null && run.ResultSet.StoreId == 0;
            if (GUILayout.Button(new GUIContent(run.ResultSet != null && run.ResultSet.StoreId > 0 ? $"Saved #{run.ResultSet.StoreId}" : "Save",
                                                "Keeps the run in the project database (SimuLean window ▸ Results ▸ Stored).")))
                Debug.Log($"[UnitySimClock] Results store #{ResultsOutput.Save(run.ResultSet, ResultsOutput.ProjectRoot)}");
            GUI.enabled = true;
            if (GUILayout.Button(new GUIContent("Show in Results", "Charts of the run in the SimuLean window.")))
                SimuLeanWindow.OpenResults(run.ResultSet);
            EditorGUILayout.EndHorizontal();
        }

        // ── Random numbers (seed mode, seed of the last run) ──────────────────

        /// <summary>
        /// Seed mode and seed through the clock's API (so changes made in Play mode are flagged and kept
        /// afterwards by <see cref="SeedPlayModePersistence"/>), the seed of the last run and Keep / New.
        /// </summary>
        private static void DrawSeedSection(UnitySimClock clock)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Random Numbers", EditorStyles.boldLabel);

            var mode = (SeedMode)EditorGUILayout.EnumPopup(
                new GUIContent("Seed Mode", "Random each run: a new seed before every run (shown below, so it can be kept). " +
                                            "Fixed: every run uses Seed."), clock.SeedMode);
            if (mode != clock.SeedMode) { Undo.RecordObject(clock, "Seed Mode"); clock.SeedMode = mode; EditorUtility.SetDirty(clock); }

            if (clock.SeedMode == SeedMode.Fixed)
            {
                int s = EditorGUILayout.IntField(
                    new GUIContent("Seed", "Model seed: every element draws from its own stream derived from it and its name " +
                                           "(a headless run with the same seed draws the same numbers)."), clock.Seed);
                if (s != clock.Seed) { Undo.RecordObject(clock, "Seed"); clock.Seed = s; EditorUtility.SetDirty(clock); }
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Seed of the last run",
                clock.HasLastRunSeed ? clock.LastRunSeed.ToString() : "— (no run yet)", EditorStyles.miniLabel);
            GUI.enabled = clock.HasLastRunSeed && !(clock.SeedMode == SeedMode.Fixed && clock.Seed == clock.LastRunSeed);
            if (GUILayout.Button(new GUIContent("Keep", "Fixed mode with the seed of the last run (reproduce it)."), GUILayout.Width(50)))
            { Undo.RecordObject(clock, "Keep Seed"); clock.KeepLastRunSeed(); EditorUtility.SetDirty(clock); }
            GUI.enabled = true;
            if (GUILayout.Button(new GUIContent("New", "Draw a new random seed and fix it for the next runs."), GUILayout.Width(50)))
            { Undo.RecordObject(clock, "New Seed"); clock.DrawNewSeed(); EditorUtility.SetDirty(clock); }
            EditorGUILayout.EndHorizontal();

            if (clock.SeedMode == SeedMode.RandomEachRun)
                EditorGUILayout.LabelField("Next run: a new random seed (shown here when it starts).", EditorStyles.miniLabel);
            if (Application.isPlaying && clock.SeedSettingsChangedInPlayMode)
                EditorGUILayout.LabelField("Seed settings changed in Play mode are kept when Play mode ends.", EditorStyles.miniLabel);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the first active <see cref="IExperimenter"/> in the scene, or null.
        /// Only valid during Play mode.
        /// </summary>
        private static IExperimenter FindExperimenterInScene()
        {
            if (!Application.isPlaying) return null;
            foreach (var mb in UnityCompat.FindAll<MonoBehaviour>())
                if (mb is IExperimenter exp) return exp;
            return null;
        }

        private static string FormatSimTime(double s)
        {
            if (s < 60)   return $"{s:F2} s";
            if (s < 3600) return $"{s / 60:F2} min  ({s:F0} s)";
            return              $"{s / 3600:F2} h  ({s:F0} s)";
        }
    }
}
