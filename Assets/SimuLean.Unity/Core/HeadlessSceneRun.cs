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
    /// statistics (<see cref="Results"/>). Works in Edit and Play mode.
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

        private bool recordTimeline;   // the state intervals (Gantt, G3c)

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

        /// <summary>Starts a run of an already extracted <paramref name="config"/> on a worker thread (time series every
        /// <paramref name="seriesInterval"/>; 0 = none).</summary>
        public static HeadlessSceneRun Start(SimulationConfig config, double seriesInterval = 0, bool timeline = false)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var run = new HeadlessSceneRun(config, seriesInterval) { recordTimeline = timeline };
            run.watch.Start();
            Task.Run(() => run.Execute(config));
            return run;
        }

        /// <summary>Stops the run at the next progress slice; no results are produced.</summary>
        public void Cancel() => cancelRequested = true;

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
                if (recordTimeline) JobRecorder.Attach(factory);   // the job Gantt (G3d)
                factory.StartModel();

                // Slices only for progress / cancel; AdvanceClock(t) leaves the clock at t (the recorder keeps whole intervals).
                var recorder = SeriesInterval > 0 ? new SeriesRecorder(factory, SeriesInterval) : null;
                double slice = MaxTime / ProgressSlices;
                for (int i = 1; i <= ProgressSlices; i++)
                {
                    if (cancelRequested) { watch.Stop(); state = RunState.Cancelled; return; }
                    if (clock.IsStopped) break;   // an end condition ended the run (S2)
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
