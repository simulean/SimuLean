using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimuLean.Results;
using SimuLean.Headless;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// One headless run of the open scene ("Run Headless" on <see cref="UnitySimClock"/>): the model
    /// is extracted on the main thread (<see cref="UnityModelExtractor"/>, clock settings included:
    /// max time, warm-up, seed, calendar), rebuilt with <see cref="HeadlessModelFactory"/> and run to
    /// the end on a worker thread, with no views. The scene is not touched; the result is the run's
    /// statistics (<see cref="Results"/>). Works in Edit and Play mode. With the out-of-editor runner
    /// (<see cref="ExternalRunner"/>) the run is a one-run batch there (≈ 10× faster; the progress then jumps to the end,
    /// the runner reports whole runs); an unseeded model draws its seed here, as a clock would, and the run records it.
    /// </summary>
    public sealed class HeadlessSceneRun
    {
        public enum RunState { Running, Finished, Failed, Cancelled }

        private const int ProgressSlices = 200;

        private volatile RunState state = RunState.Running;
        private volatile bool cancelRequested;
        private double simTime;                 // read/written through Volatile (worker → main thread)
        private readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();

        public RunState State => state;
        public bool IsRunning => state == RunState.Running;
        /// <summary>The run's work on its worker thread: complete once the run has ended (its state, results and error are
        /// final). A caller that must wait for the end waits on it, not in a sleep loop.</summary>
        public Task Completion { get; private set; }
        public double MaxTime { get; }
        public double SimTime => Volatile.Read(ref simTime);
        public double Progress => state == RunState.Finished ? 1.0 : MaxTime > 0 ? Math.Min(1.0, SimTime / MaxTime) : 0;
        public double WallSeconds => watch.Elapsed.TotalSeconds;
        /// <summary>Sim time units per real second (so far, or over the whole run when finished).</summary>
        public double EffectiveSpeed => WallSeconds > 0 ? SimTime / WallSeconds : 0;
        public int ElementCount { get; }
        public int? Seed { get; }
        /// <summary>The run's statistics (<see cref="RunResults"/>; null until finished).</summary>
        public RunResults Results { get; private set; }
        /// <summary>The run as a result set (exportable, <see cref="ResultsOutput"/>); null until finished.</summary>
        public ResultSet ResultSet { get; private set; }
        public Exception Error { get; private set; }

        private readonly string modelName, projectRoot;
        private readonly DateTime startedAt = DateTime.Now;

        /// <summary>Interval of the recorded time series (0 = none).</summary>
        public double SeriesInterval { get; }

        private bool recordTimeline;   // the state intervals (Gantt)

        private HeadlessSceneRun(SimulationConfig config, double seriesInterval)
        {
            modelName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;   // main thread
            projectRoot = ResultsOutput.ProjectRoot;
            SeriesInterval = Math.Max(0, seriesInterval);
            MaxTime = config.MaxSimulationTime > 0 ? config.MaxSimulationTime : 100.0;
            ElementCount = config.Elements.Count;
            Seed = config.UseFixedSeed ? (int?)config.Seed : null;
        }

        /// <summary>
        /// Extracts the scene (main thread) and starts the run on a worker thread. Uses the scene's
        /// <see cref="UnityModelExtractor"/> (and its model root) when there is one.
        /// </summary>
        public static HeadlessSceneRun Start(double seriesInterval = 0, bool timeline = false) => Start(Extract(), seriesInterval, timeline);

        /// <summary>Where the run goes (<see cref="ExternalRunner.Executor"/>; null = in the editor). Main thread.</summary>
        public string RanIn => executor?.RunnerPath != null ? "runner (.NET, out of the editor)" : "editor";

        private ExternalRunner.Executor executor;

        /// <summary>Starts a run of an already extracted <paramref name="config"/> on a worker thread (time series every
        /// <paramref name="seriesInterval"/>; 0 = none).</summary>
        public static HeadlessSceneRun Start(SimulationConfig config, double seriesInterval = 0, bool timeline = false)
            => Start(config, seriesInterval, timeline, ExternalRunner.Prepare());

        /// <summary>As <see cref="Start(SimulationConfig, double, bool)"/>, run by <paramref name="executor"/> (the runner
        /// when it has one, else in the editor).</summary>
        public static HeadlessSceneRun Start(SimulationConfig config, double seriesInterval, bool timeline, ExternalRunner.Executor executor)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var run = new HeadlessSceneRun(config, seriesInterval) { recordTimeline = timeline, executor = executor };
            run.watch.Start();
            run.Completion = executor?.RunnerPath != null ? Task.Run(() => run.ExecuteOutside(config)) : Task.Run(() => run.Execute(config));
            return run;
        }

        private readonly CancellationTokenSource cancel = new CancellationTokenSource();

        // the run as a one-run batch in the runner (the same run: the model, its seed, the horizon, series and timeline)
        private void ExecuteOutside(SimulationConfig config)
        {
            try
            {
                int seed = Seed ?? new SimClock(null).Context.Seed;   // an unseeded model: a fresh seed, as a clock draws it
                var batch = new SimuLean.Optimization.StudyBatch
                {
                    Name = modelName, BaseSeed = seed, RunTime = MaxTime, SeriesInterval = SeriesInterval, RecordTimeline = recordTimeline,
                    Threads = 1,
                };
                batch.Scenarios.Add(new SimuLean.Optimization.StudyBatch.Scenario { Name = "Run", Replications = 1, Model = config });
                var set = executor.Run(batch, cancel.Token);
                if (cancelRequested) { watch.Stop(); state = RunState.Cancelled; return; }
                var only = set.Runs.Count == 1 ? set.Runs[0] : null;
                if (only == null || only.Error != null) throw new InvalidOperationException(only?.Error ?? "the run did not complete");
                Results = only.Results;
                Volatile.Write(ref simTime, Results.Info.EndTime);
                ResultSet = ResultsOutput.SingleRun(Results, modelName, startedAt, watch.Elapsed.TotalSeconds);
                ResultSet.Metadata.Add(new KeyValuePair<string, string>("ran_in", executor.Description));
                ResultsOutput.Publish(ResultSet, projectRoot);
                watch.Stop();
                state = RunState.Finished;
            }
            catch (Exception ex)
            {
                watch.Stop();
                Error = ex;
                state = RunState.Failed;
                Debug.LogError($"[HeadlessSceneRun] {ex}");
            }
        }

        /// <summary>Stops the run at the next progress slice; no results are produced.</summary>
        public void Cancel() { cancelRequested = true; cancel.Cancel(); }

        private static SimulationConfig Extract()
        {
            var existing = UnityCompat.FindAny<UnityModelExtractor>();
            return SceneExtractor.Extract(existing != null ? existing.modelRoot : null).Config;
        }

        private void Execute(SimulationConfig config)
        {
            try
            {
                var clock = new SimClock(Seed);
                var factory = new HeadlessModelFactory(clock, enableLogging: false);
                factory.BuildModel(config);
                if (recordTimeline) JobRecorder.Attach(factory);   // the job Gantt
                factory.StartModel();

                // Slices only for progress / cancel; AdvanceClock(t) leaves the clock at t (the recorder keeps whole intervals).
                var recorder = SeriesInterval > 0 ? new SeriesRecorder(factory, SeriesInterval) : null;
                double slice = MaxTime / ProgressSlices;
                for (int i = 1; i <= ProgressSlices; i++)
                {
                    if (cancelRequested) { watch.Stop(); state = RunState.Cancelled; return; }
                    if (clock.IsStopped) break;   // an end condition ended the run
                    double t = i == ProgressSlices ? MaxTime : slice * i;
                    if (recorder != null) recorder.RunTo(t, final: i == ProgressSlices);
                    else clock.AdvanceClock(t);
                    Volatile.Write(ref simTime, clock.GetSimulationTime());
                }

                var results = RunResults.Capture(factory, modelName, recordTimeline);
                recorder?.AddTo(results);
                Results = results;
                ResultSet = ResultsOutput.SingleRun(results, modelName, startedAt, watch.Elapsed.TotalSeconds);
                ResultsOutput.Publish(ResultSet, projectRoot);
                watch.Stop();
                state = RunState.Finished;
            }
            catch (Exception ex)
            {
                watch.Stop();
                Error = ex;
                state = RunState.Failed;
                Debug.LogError($"[HeadlessSceneRun] {ex}");
            }
        }
    }
}
