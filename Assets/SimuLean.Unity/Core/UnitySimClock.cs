using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Component that instantiates the clock of the simulation.
    /// <remarks>
    /// UnitySimClock initializes all sim elements, starts and advances the simulation.
    /// It must be just one component of UnitySimClock script.
    /// </remarks>
    /// </summary>
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Simulation, "Simulation Clock", Description = "Drives the run: horizon, speed, calendar, seed, warm-up (one per scene). Created with its HUD, inspector panel, visual detail and navigation camera", Singleton = true)]
    public class UnitySimClock : MonoBehaviour
    {
        private static UnitySimClock instance;
        /// <summary>The scene's clock, or null when there is none (views and inspectors check it;
        /// It used to throw, so every null check was dead and an inspector without a clock failed).</summary>
        public static UnitySimClock Instance
        {
            get
            {
                if (instance == null) instance = UnityCompat.FindAny<UnitySimClock>();
                return instance;
            }
        }

        [SerializeField, Tooltip("Simulation time units per real second (1 = real time).")] private double timeScale = 1;

        bool simOn = false;
        bool simStarted = false;
        bool simStopped = false;
        bool simRestarted = false;

        public SimClock clock = new SimClock();

        // SimEvents is created automatically — no ScriptableObject asset required.
        private readonly SimEvents simEvents = new SimEvents();

        //Optional
        [SerializeField] private double maxTime = 1000.0;

        [Tooltip("Random each run (default): a new model seed is drawn before every run and shown, so the run can be " +
                 "reproduced. Fixed: every run uses Seed (reproducible; exported as the headless seed).")]
        [SerializeField] private SeedMode seedMode = SeedMode.RandomEachRun;

        [Tooltip("Model seed used in Fixed mode. Every element draws from its own stream derived from this seed and its " +
                 "name, so a headless run with the same seed draws the same numbers per element.")]
        [SerializeField] private int seed = 0;

        [Tooltip("Warm-up (simulated time): element statistics are reset at this time. 0 = none (default). " +
                 "Exported as the headless warm-up.")]
        [SerializeField] private double warmupTime = 0.0;

        /// <summary>Model seed used in <see cref="SeedMode.Fixed"/> mode (see the Seed region).</summary>
        public int Seed { get => seed; set { if (seed != value) { seed = value; MarkSeedChanged(); } } }

        /// <summary>Warm-up time of the scene run (0 = none).</summary>
        public double WarmupTime { get => warmupTime; set => warmupTime = value; }

        [Header("Calendar")]
        [Tooltip("Date and time of simulation time 0, e.g. 2026-01-01 06:00 (ISO or dd/MM/yyyy HH:mm). Empty = 2000-01-01 00:00.")]
        [SerializeField] private string startDateTime = "";

        [Tooltip("Seconds per simulation time unit (1 = the clock counts seconds).")]
        [SerializeField] private double timeUnitSeconds = 1.0;

        [Tooltip("The model's holidays (dates, e.g. 2026-12-25): every shift of the model is off on them — one working " +
                 "calendar for every station and agenda, on top of each shift's own holidays.")]
        [SerializeField] private List<string> holidays = new List<string>();

        /// <summary>The model's holidays.</summary>
        public List<string> Holidays => holidays;

        private List<SElement> elements = new List<SElement>();

        Timer timer;

        // Set to true by the editor Run button before entering Play mode so Start()
        // auto-calls RunSimulation() immediately after initialisation.
        [SerializeField, HideInInspector] private bool _startOnPlay = false;

        [Tooltip("Start the simulation automatically when entering Play mode (no Run button needed).")]
        [SerializeField] private bool autoStartOnPlay = true;
        // Records Unity time at the moment Pause was pressed (used to fix the resume jump).
        private float _pauseStartTime = 0f;

        [Header("Pacing (large speed-ups)")]
        [Tooltip("Wall-clock budget (ms) for advancing the simulation in one frame. 0 = legacy behaviour (catch up with the " +
                 "wall clock in one AdvanceClock call). > 0 = paced mode: the clock advances in slices until the budget is used, " +
                 "and a simulation that cannot keep up with TimeScale drops the lag instead of freezing the view (double-precision anchors).")]
        [SerializeField] private float frameBudgetMs = 0f;
        [Tooltip("Escape quits the application (legacy). Applications with their own menu set this to false.")]
        [SerializeField] private bool escapeQuits = true;

        [Tooltip("Maximum speed: ignore Time Scale and advance as far as the frame budget allows (paced mode). Default off.")]
        [SerializeField] private bool runAsFastAsPossible;

        [Header("End condition")]
        [Tooltip("Stop the run when these elements have received Stop At Count items between them: sinks, or e.g. the queues " +
                 "that hold the finished product (Max Time still caps it). Empty = off.")]
        [SerializeField] private System.Collections.Generic.List<SElement> stopAt = new System.Collections.Generic.List<SElement>();
        [Tooltip("Items the Stop At elements must receive to stop the run (0 = off).")]
        [Min(0)] [SerializeField] private int stopAtCount;
        [Tooltip("Stop the run when nothing is left to do: every item out, at rest in a buffer without outputs or surplus " +
                 "nothing will take (or the model deadlocked / stalled).")]
        [SerializeField] private bool stopWhenEmpty;

        /// <summary>End condition: the elements and the count that stop the run (any elements).</summary>
        public System.Collections.Generic.List<SElement> StopAt => stopAt;
        public int StopAtCount { get => stopAtCount; set => stopAtCount = System.Math.Max(0, value); }
        /// <summary>End condition: stop when nothing is left to do (or deadlocked / stalled).</summary>
        public bool StopWhenEmpty { get => stopWhenEmpty; set => stopWhenEmpty = value; }

        [Header("Results")]
        [Tooltip("Export the run's statistics when it reaches Max Time (Play mode) to the results folder of the project settings. " +
                 "Nothing = no file (the SimuLean window's Results tab still shows the last run).")]
        [SerializeField] private SimuLean.Results.ResultFormats exportOnFinish = SimuLean.Results.ResultFormats.None;
        [Tooltip("Interval of the time series (content, throughput, states per interval) recorded by Run Headless and by a Play run " +
                 "(Results tab, dashboards). 0 = none.")]
        [Min(0)] [SerializeField] private double seriesInterval = 0;

        [Tooltip("Keep every state interval of elements and resources (the Gantt charts; G3c). Off by default: the results grow with every state change.")]
        [SerializeField] private bool recordTimeline = false;
        /// <summary>A run (Play, Run Headless) keeps its state intervals — the Gantt (G3c; off by default).</summary>
        public bool RecordTimeline { get => recordTimeline; set => recordTimeline = value; }

        /// <summary>Formats the run is exported to when it reaches its max time (none by default).</summary>
        public SimuLean.Results.ResultFormats ExportOnFinish { get => exportOnFinish; set => exportOnFinish = value; }
        /// <summary>Interval of the time series of a run of the scene, headless or in Play (0 = none).</summary>
        public double SeriesInterval { get => seriesInterval; set => seriesInterval = System.Math.Max(0, value); }
        private double paceWall, paceSim;        // paced mode: wall time (s, realtime) and sim time of the anchor
        // Play runs with a series interval (G2b): the clock advances through the recorder, which samples at the interval
        // boundaries (the same events run: AdvanceClock is called at the boundaries, then up to the target)
        [System.NonSerialized] private SimuLean.Results.SeriesRecorder recorder;

        private void AdvanceTo(double t)
        {
            if (recorder != null) recorder.RunTo(t, final: false);
            else clock.AdvanceClock(t);
        }

        /// <summary>
        /// The run going on, as results (read-only): its statistics now (<see cref="SimuLean.Results.RunResults.Capture"/>)
        /// and the series recorded so far (with a <see cref="SeriesInterval"/>). Null before a run. For dashboards and views.
        /// </summary>
        public SimuLean.Results.RunResults CaptureLive(bool timeline = false)
        {
            if (Model == null) return null;
            var results = SimuLean.Results.RunResults.Capture(Model, gameObject.scene.name, timeline || recordTimeline);
            if (recorder != null) foreach (var ts in recorder.Series) results.Series.Add(Copy(ts));
            return results;
        }

        private static SimuLean.Results.TimeSeries Copy(SimuLean.Results.TimeSeries ts)
        {
            var c = new SimuLean.Results.TimeSeries(ts.Scope, ts.Name, ts.Metric);
            c.Times.AddRange(ts.Times);
            c.Values.AddRange(ts.Values);
            return c;
        }
        private readonly System.Diagnostics.Stopwatch frameWatch = new System.Diagnostics.Stopwatch();

        #region Getters
        public double MaxTime { get => maxTime; set => maxTime = value; }

        /// <summary>Calendar start (text as configured; empty = default calendar).</summary>
        public string StartDateTime { get => startDateTime; set => startDateTime = value; }

        /// <summary>Seconds per simulation time unit.</summary>
        public double TimeUnitSeconds { get => timeUnitSeconds; set => timeUnitSeconds = value; }

        /// <summary>Builds the calendar configured on this clock.</summary>
        public SimCalendar CreateCalendar()
        {
            double unit = timeUnitSeconds > 0 ? timeUnitSeconds : 1.0;
            if (string.IsNullOrWhiteSpace(startDateTime)) return SimCalendar.Undated(unit);   // the unit applies without a start date too (S5)
            if (SimCalendar.TryParseDate(startDateTime, out System.DateTime start))
                return new SimCalendar(start, unit);
            Debug.LogWarning($"[UnitySimClock] Invalid start date '{startDateTime}' — using the default calendar.");
            return SimCalendar.Undated(unit);
        }

        /// <summary>Calendar date of the current simulation time.</summary>
        public System.DateTime CurrentDateTime => clock.GetDateTime();
        public List<SElement> Elements { get => elements; set => elements = value; }

        public SimEvents SimEvents => simEvents;

        /// <summary>Wall-clock budget per frame for the simulation (ms); 0 = legacy catch-up (see the field tooltip).</summary>
        public float FrameBudgetMs { get => frameBudgetMs; set { frameBudgetMs = Mathf.Max(0, value); Reanchor(); } }

        private static int escapeConsumedFrame = -1;

        /// <summary>
        /// Marks this frame's Esc key press as used by another component (e.g. an optional panel that closes on Esc and
        /// runs before the clock), so <see cref="EscapeQuits"/> does not also quit. No effect when nobody calls it.
        /// </summary>
        public static void ConsumeEscape() => escapeConsumedFrame = Time.frameCount;

        /// <summary>Escape quits the application (legacy default true).</summary>
        public bool EscapeQuits { get => escapeQuits; set => escapeQuits = value; }

        /// <summary>
        /// "Maximum speed": ignore <see cref="TimeScale"/> and advance as far as the frame budget allows every frame (default
        /// budget 40 ms when <see cref="FrameBudgetMs"/> is 0), so the view stays responsive while the CPU runs the model.
        /// </summary>
        public bool RunAsFastAsPossible
        {
            get => runAsFastAsPossible;
            set { if (runAsFastAsPossible != value) { runAsFastAsPossible = value; Reanchor(); } }
        }

        /// <summary>True when the paced (budgeted) driving loop is active.</summary>
        public bool Paced => frameBudgetMs > 0 || runAsFastAsPossible;

        /// <summary>Effective simulation speed of the last frames (sim s per real s; paced mode), for HUDs.</summary>
        public double EffectiveSpeed { get; private set; }
        private double effWall, effSim;

        private static double WallNow => Time.realtimeSinceStartupAsDouble;

        /// <summary>Restarts the paced-mode anchor at the current simulation time (no jump, lag dropped).</summary>
        private void Reanchor()
        {
            paceWall = WallNow;
            paceSim = clock.GetSimulationTime();
            // back to the legacy loop: its float timer must continue from the current simulation time
            if (!Paced && timer != null && timeScale > 0)
                timer.PastTime = (float)((simStopped ? _pauseStartTime : Time.time) - clock.GetSimulationTime() / timeScale);
        }

        // Read-only state exposed for the custom editor
        public bool SimOn      => simOn;
        public bool SimPaused  => simStopped;
        public bool SimStarted => simStarted;

        /// <summary>Starts the simulation automatically in Start() when true.</summary>
        public bool AutoStartOnPlay { get => autoStartOnPlay; set => autoStartOnPlay = value; }

        /// <summary>
        /// Simulation time units per real second. Setting it while running re-anchors the
        /// timer so the simulation continues from the current time without jumping
        /// </summary>
        public double TimeScale
        {
            get => timeScale;
            set
            {
                if (value <= 0 || value == timeScale) return;
                if (Paced) { Reanchor(); timeScale = value; return; }
                if (timer != null)
                {
                    float refTime = simStopped ? _pauseStartTime : Time.time;
                    double elapsedSim = (refTime - timer.PastTime) * timeScale;
                    timer.PastTime = (float)(refTime - elapsedSim / value);
                }
                timeScale = value;
            }
        }

        /// <summary>
        /// Continuously-interpolated simulation time for smooth animation.
        /// Unlike clock.GetSimulationTime() (which only advances on events),
        /// this advances every frame via the same timer formula used by Update().
        /// Automatically freezes while paused.
        /// </summary>
        /// <summary>Changes the scene's extracted config before each run's build (an experiment's scenario); null = none.</summary>
        public System.Action<SimuLean.Serialization.SimulationConfig> ConfigureModel { get; set; }

        /// <summary>The model of the current run (its engine objects, for metrics), or null before the first run.</summary>
        public SimuLean.Headless.HeadlessModelFactory Model { get; private set; }

        public float SmoothSimTime
        {
            get
            {
                if (timer == null) return 0f;
                if (Paced) return (float)clock.GetSimulationTime();
                float refTime = simStopped ? _pauseStartTime : Time.time;
                return (refTime - timer.PastTime) * (float)timeScale;
            }
        }
        #endregion

        #region Events Set Up
        private void OnEnable()
        {
            simEvents.OnSimStart         += RestartSim;
            simEvents.OnSimStop          += PauseSim;
            simEvents.OnSimResume        += ResumeSim;
            simEvents.OnExperimentFinish += FinishSim;
        }

        private void OnDisable()
        {
            simEvents.OnSimStart         -= RestartSim;
            simEvents.OnSimStop          -= PauseSim;
            simEvents.OnSimResume        -= ResumeSim;
            simEvents.OnExperimentFinish -= FinishSim;
        }
        #endregion

        void Awake()
        {
            SimLogUnityBridge.Install();   // engine logs → Unity console (players; the editor installs it on load)
            ItemViews.Install();           // item views (labels, block looks) get their item when it gets a visual
            instance = this;

            simOn = false;
        }

        void Start()
        {
            timer = new Timer();
            // Auto-start if the editor Run button was pressed before entering Play mode.
            if (_startOnPlay || autoStartOnPlay)
            {
                _startOnPlay = false;
                RunSimulation();
            }
        }

        // The finished run's statistics (read-only): the Results tab's last run, exported in exportOnFinish. Before
        // OnSimFinish, whose subscribers may start the next run (in-scene experiments).
        private void PublishRun()
        {
            if (Model == null) return;
            try
            {
                recorder?.RunTo(clock.GetSimulationTime(), final: true);   // the last, shorter interval
                var results = SimuLean.Results.RunResults.Capture(Model, gameObject.scene.name, recordTimeline);
                recorder?.AddTo(results);
                var set = ResultsOutput.SingleRun(results, gameObject.scene.name, System.DateTime.Now);
                foreach (string path in ResultsOutput.Export(set, exportOnFinish, ResultsOutput.ProjectRoot))
                    Debug.Log($"[UnitySimClock] Results: {path}");
            }
            catch (System.Exception e) { Debug.LogWarning($"[UnitySimClock] Results of the run not exported: {e.Message}"); }
        }

        void Update()
        {
            if (simOn && !simRestarted && !simStopped)
            {
                if (simStarted && Paced)
                {
                    AdvancePaced();
                    if (clock.GetSimulationTime() >= maxTime || clock.IsStopped)   // the horizon or an end condition (S2)
                    {
                        simOn = false;
                        PublishRun();
                        simEvents.OnSimFinish?.Invoke();
                    }
                }
                else if (simStarted)
                {
                    // Never past the end: a large TimeScale would otherwise run far beyond maxTime in one frame.
                    AdvanceTo(System.Math.Min((Time.time - timer.PastTime + Time.deltaTime) * timeScale, maxTime));

                    if (clock.GetSimulationTime() >= maxTime || clock.IsStopped)   // the horizon or an end condition (S2)
                    {
                        // Disable first so any subscriber that calls RestartSim toggles simOn back to true.
                        simOn = false;
                        PublishRun();
                        simEvents.OnSimFinish?.Invoke();
                    }
                }
                else
                {
                    // Calendar first: elements may convert dates to sim time while initialising.
                    clock.Context.Calendar = CreateCalendar();

                    // Model seed of this run (before the elements create their per-element streams).
                    ApplyRunSeed();

                    // The scene's model, built as in a headless run (one build path), bound to the scene, then started.
                    // A model that cannot be built ends the run with its error, once (it was extracted and retried every
                    // frame, logging a new seed each time).
                    SimuLean.Headless.HeadlessModelFactory model;
                    try { model = SceneModelBuilder.Build(clock, elements, null, ConfigureModel); }
                    catch (System.Exception e)
                    {
                        simOn = false;
                        Debug.LogError("[SimuLean] The model cannot be built: " + e.Message);
                        return;
                    }
                    Model = model;
                    if (recordTimeline) SimuLean.Results.JobRecorder.Attach(model);   // the job Gantt (G3d)
                    model.StartModel();
                    recorder = seriesInterval > 0 ? new SimuLean.Results.SeriesRecorder(model, seriesInterval) : null;
                    foreach (SElement theElem in elements)
                        theElem.OnRunStarted();
                    foreach (var team in UnityCompat.FindAll<UnityResourceTeam>())
                        team.OnRunStarted();
                    if (Paced) Reanchor();
                    else AdvanceTo(System.Math.Min((Time.time - timer.PastTime + Time.deltaTime) * timeScale, maxTime));
                    simStarted = true;
                    effWall = WallNow; effSim = clock.GetSimulationTime();
                }
            }

            // the items walking by themselves, where they are now (a view of the engine's walks)
            if (simStarted && Model != null) ItemWalkView.Draw(Model, clock.GetSimulationTime());

            if (escapeQuits && Input.GetKeyDown(KeyCode.Escape) && escapeConsumedFrame != Time.frameCount)
                this.Quit();
        }

        /// <summary>
        /// Paced mode: advance towards the wall-clock target (anchor + elapsed × TimeScale, or the end with
        /// <see cref="RunAsFastAsPossible"/>) in slices until the frame budget is spent; if the target is not reached the
        /// anchor restarts at the reached time (the lag is dropped, the view never freezes waiting to catch up).
        /// </summary>
        private void AdvancePaced()
        {
            double budget = (frameBudgetMs > 0 ? frameBudgetMs : 40f) / 1000.0;
            double now = WallNow;
            double target = runAsFastAsPossible ? maxTime : paceSim + (now - paceWall) * timeScale;
            if (target > maxTime) target = maxTime;
            double t = clock.GetSimulationTime();
            double slice = runAsFastAsPossible ? 900.0 : System.Math.Max(1.0, timeScale * 0.004);
            frameWatch.Restart();
            while (t < target)
            {
                double next = System.Math.Min(target, t + slice);
                AdvanceTo(next);
                t = clock.GetSimulationTime();
                if (simStopped || !simOn) break;
                if (frameWatch.Elapsed.TotalSeconds >= budget) break;
            }
            if (t < target - 1e-6 || runAsFastAsPossible) { paceWall = WallNow; paceSim = t; }
            // effective speed over ~0.5 s windows
            double w = WallNow - effWall;
            if (w >= 0.5) { EffectiveSpeed = (t - effSim) / w; effWall = WallNow; effSim = t; }
        }

        /// <summary>
        /// Switches Off Unity TimeScale
        /// </summary>
        public void PauseSim()
        {
            if (!simStopped)
            {
                _pauseStartTime = Time.time;  // remember when we paused
                simStopped = true;
            }
        }        
        
        /// <summary>
        /// Switches Unity TimeScale
        /// </summary>
        public void ResumeSim()
        {
            if (simStopped && timer != null)
            {
                // Shift the timer reference forward by the pause duration so the
                // AdvanceClock formula doesn't jump by wall-clock pause time.
                timer.PastTime += Time.time - _pauseStartTime;
            }
            simStopped = false;
            if (Paced) Reanchor();   // legacy loop: the PastTime shift above is the original behaviour
        }


        /// <summary>
        /// Resets and restarts Simulation
        /// </summary>
        public void RestartSim(double maxTime = 0)
        {
            if (simStarted)
            {
                foreach (var team in UnityCompat.FindAll<UnityResourceTeam>()) team.ClearViews();   // before the reset
                this.ResetSim();
                foreach (SElement theElem in elements)
                    theElem.ClearViews();
            }

            simStarted = false;
            simRestarted = false;
            simStopped = false;

            this.maxTime = maxTime;
            timer.OnRestart();

            simOn = !simOn;
        }

        /// <summary>
        /// Resets clock and stops simulation.
        /// </summary>
        public void ResetSim()
        {
            simRestarted = true;
            simOn = false;
            NewModelClock();
        }

        /// <summary>
        /// A run builds its model on a new <see cref="SimClock"/>: the previous model's pending events stay on the old
        /// clock, which is not advanced any more, and the new context starts without the old elements and lists
        /// (reusing one clock left the previous model running alongside the new one).
        /// </summary>
        private void NewModelClock() { clock = new SimClock(); recorder = null; ItemWalkView.Clear(); }

        public void FinishSim()
        {
            simOn = false;
            ResetSim();
        }

        /// <summary>
        /// Starts the simulation if not yet started, or restarts it if already run.
        /// The simulation does not start automatically on Play; call this explicitly
        /// (e.g. from an Inspector button) or let an <see cref="IExperimenter"/> drive it.
        /// </summary>
        public void RunSimulation()
        {
            if (simStarted)
                RestartSim(maxTime);
            else
            {
                // Anchor the timer to NOW so the accumulated Unity time before the
                // Run button was pressed is not counted as simulation time.
                timer?.OnRestart();
                simOn = true;
            }
        }

        /// <summary>
        /// Immediately stops and fully resets the simulation clock.
        /// Destroys all in-scene visual item GameObjects owned by simulation elements.
        /// After this call the sim is in the same state as just after Play was pressed.
        /// </summary>
        public void StopSimulation()
        {
            simOn       = false;
            simStopped  = false;
            simStarted  = false;
            simRestarted = false;
            // Destroy all in-scene item GameObjects tracked by simulation elements, and the teams'.
            foreach (var team in UnityCompat.FindAll<UnityResourceTeam>()) team.ClearViews();
            foreach (SElement theElem in elements)
                theElem.ClearViews();
            NewModelClock();
        }

        #region Seed
        // Kept as one block: the seed mode, the seed of the last run and the actions of the inspector / HUD.

        [System.NonSerialized] private int lastRunSeed;
        [System.NonSerialized] private bool hasLastRunSeed;
        [System.NonSerialized] private bool seedChangedInPlayMode;

        /// <summary>Random each run (a new seed drawn before every run) or Fixed (<see cref="Seed"/>).</summary>
        public SeedMode SeedMode
        {
            get => seedMode;
            set { if (seedMode != value) { seedMode = value; MarkSeedChanged(); } }
        }

        /// <summary>Model seed of the last / current run (valid when <see cref="HasLastRunSeed"/>).</summary>
        public int LastRunSeed => lastRunSeed;

        /// <summary>True once a run has started in this session (<see cref="LastRunSeed"/> is set).</summary>
        public bool HasLastRunSeed => hasLastRunSeed;

        /// <summary>True when the seed settings were changed during Play mode (the editor keeps them afterwards).</summary>
        public bool SeedSettingsChangedInPlayMode => seedChangedInPlayMode;

        /// <summary>Switches to <see cref="SeedMode.Fixed"/> with <paramref name="value"/>.</summary>
        public void UseFixedSeed(int value)
        {
            Seed = value;
            SeedMode = SeedMode.Fixed;
        }

        /// <summary>Keeps the seed of the last run: Fixed mode with <see cref="LastRunSeed"/>. No effect before a run.</summary>
        public void KeepLastRunSeed()
        {
            if (hasLastRunSeed) UseFixedSeed(lastRunSeed);
        }

        /// <summary>Draws a new random seed and fixes it for the next runs (shown before running). Returns it.</summary>
        public int DrawNewSeed()
        {
            int s = SimContext.NewRandomSeed();
            UseFixedSeed(s);
            return s;
        }

        /// <summary>Back to <see cref="SeedMode.RandomEachRun"/>.</summary>
        public void UseRandomSeedEachRun() => SeedMode = SeedMode.RandomEachRun;

        /// <summary>Seed the next run will use, or null when a new random one will be drawn.</summary>
        public int? NextRunSeed => SeedMode == SeedMode.Fixed ? seed : (int?)null;

        private void ApplyRunSeed()
        {
            int runSeed = NextRunSeed ?? SimContext.NewRandomSeed();
            clock.Context.Reseed(runSeed);
            lastRunSeed = runSeed;
            hasLastRunSeed = true;
            Debug.Log(seedMode == SeedMode.Fixed
                ? $"[UnitySimClock] Run seed {runSeed} (fixed)."
                : $"[UnitySimClock] Run seed {runSeed} (random each run — Keep it on the clock or the HUD to reproduce this run).");
        }

        private void MarkSeedChanged()
        {
            if (Application.isPlaying) seedChangedInPlayMode = true;
        }

        #endregion

        public void Quit()
        {
#if UNITY_EDITOR
            Debug.Log("Quit Scene");
            UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
        }
    }

    /// <summary>How <see cref="UnitySimClock"/> picks the model seed of a run.</summary>
    public enum SeedMode
    {
        /// <summary>A new random seed before every run (shown, so the run can be reproduced). Default.</summary>
        [InspectorName("Random each run")] RandomEachRun = 0,
        /// <summary>Every run uses <see cref="UnitySimClock.Seed"/>.</summary>
        [InspectorName("Fixed")] Fixed = 1,
    }
}
