using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimuLean.Data;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>
    /// A study with its scenarios already applied: each scenario's model (the levels applied by the
    /// study's parameters, whatever they are — catalog keys, team members, tables read with the editor's reader), its
    /// replications, the metrics and the run settings. <see cref="Study.Run"/> runs through it, so a batch run in the editor
    /// and one sent to the out-of-editor runner (<c>{"format": 1, "kind": "batch", "batch": {…}}</c>,
    /// <see cref="Serialize"/>) are the same code on the same data. A scenario whose levels could not be applied keeps the
    /// error (<see cref="Scenario.Error"/>) and its runs are recorded with it.
    /// </summary>
    public sealed class StudyBatch
    {
        public sealed class Scenario
        {
            public string Name = "";
            /// <summary>The factor labels and values (the result set's scenario parameters).</summary>
            public List<KeyValuePair<string, string>> Parameters = new List<KeyValuePair<string, string>>();
            public int Replications = 1;
            /// <summary>The model with the scenario's levels applied (null when they could not be: <see cref="Error"/>).</summary>
            public SimulationConfig Model;
            public string Error;
        }

        public string Name = "Study";
        /// <summary>The study's replications (the result set's metadata; each scenario has its own count).</summary>
        public int Replications = 1;
        public int BaseSeed = 1;
        /// <summary>Simulated time of each run (0 = each model's max time).</summary>
        public double RunTime;
        public double SeriesInterval;
        public bool RecordTimeline;
        public bool RecordStatistics = true;
        public double Confidence = 0.95;
        /// <summary>Runs at once (0 = <see cref="Study.DefaultThreads"/>).</summary>
        public int Threads;
        public List<ModelMetric> Metrics = new List<ModelMetric>();
        public List<Scenario> Scenarios = new List<Scenario>();

        /// <summary>Runs every scenario × replication headless (in parallel) and returns the result set.
        /// <paramref name="progress"/> gets (runs done, runs in all); <paramref name="runDone"/> each run as soon as it ends
        /// (its index in the set's run order, the run), from the worker thread that ran it.</summary>
        public ResultSet Run(CancellationToken token = default, Action<int, int> progress = null, Action<int, ResultRun> runDone = null)
        {
            var objectives = new SimulationObjective[Scenarios.Count];
            var candidates = new Candidate[Scenarios.Count];
            var runs = new List<(int s, int rep)>();
            for (int i = 0; i < Scenarios.Count; i++)
            {
                var sc = Scenarios[i];
                if (sc.Model != null)
                {
                    objectives[i] = new SimulationObjective(sc.Model, new ModelParameter[0], Metrics, "0")
                    {
                        RunTime = RunTime, SeriesInterval = SeriesInterval, RecordTimeline = RecordTimeline,
                    };
                    candidates[i] = new Candidate(objectives[i].BuildSpace());
                }
                for (int r = 1; r <= Math.Max(1, sc.Replications); r++) runs.Add((i, r));
            }
            var done = new ResultRun[runs.Count];
            int count = 0;
            bool record = RecordStatistics || SeriesInterval > 0 || RecordTimeline;
            bool cancelled = false;
            try
            {
                Parallel.For(0, runs.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = Threads > 0 ? Threads : Study.DefaultThreads, CancellationToken = token },
                    k =>
                    {
                        var (i, rep) = runs[k];
                        var sc = Scenarios[i];
                        var watch = Stopwatch.StartNew();
                        RunResults results = null;
                        int seed = BaseSeed + rep - 1;
                        var run = new ResultRun { Scenario = sc.Name, Replication = rep, Seed = seed };
                        // a run that fails (a level the model cannot be built with, a metric it cannot read) is recorded
                        // with its error; the others carry on and are kept (one failure lost them all)
                        if (sc.Error != null) run.Error = sc.Error;
                        else
                            try
                            {
                                var e = objectives[i].Run(candidates[i], seed, record ? r => results = r : (Action<RunResults>)null);
                                foreach (var kv in e.Metrics) run.Metrics[kv.Key] = kv.Value;
                                run.Results = results;
                            }
                            catch (Exception ex) when (!(ex is OperationCanceledException)) { run.Error = ex.GetType().Name + ": " + ex.Message; }
                        run.ElapsedSeconds = watch.Elapsed.TotalSeconds;
                        done[k] = run;
                        runDone?.Invoke(k, run);
                        progress?.Invoke(Interlocked.Increment(ref count), runs.Count);
                    });
            }
            catch (OperationCanceledException) { cancelled = true; }   // the runs done so far are kept

            var set = new ResultSet { Name = Name, Source = "experiment", Created = DateTime.Now, Confidence = Confidence };
            set.MetricNames.AddRange(Metrics.Select(m => m.Name));
            foreach (var sc in Scenarios)
            {
                var rs = new ResultScenario { Name = sc.Name };
                rs.Parameters.AddRange(sc.Parameters);
                set.Scenarios.Add(rs);
            }
            foreach (var r in done) if (r != null) set.Runs.Add(r);
            if (cancelled) set.Metadata.Add(new KeyValuePair<string, string>("cancelled", "true"));
            var failedRuns = set.Runs.Where(r => r.Error != null).ToList();
            if (failedRuns.Count > 0)
            {
                set.Metadata.Add(new KeyValuePair<string, string>("failed_runs", failedRuns.Count.ToString(CultureInfo.InvariantCulture)));
                set.Metadata.Add(new KeyValuePair<string, string>("first_error", failedRuns[0].Id + ": " + failedRuns[0].Error));
            }
            set.Metadata.Add(new KeyValuePair<string, string>("replications", Replications.ToString(CultureInfo.InvariantCulture)));
            set.Metadata.Add(new KeyValuePair<string, string>("base_seed", BaseSeed.ToString(CultureInfo.InvariantCulture)));
            set.Metadata.Add(new KeyValuePair<string, string>("run_time", RunTime.ToString("R", CultureInfo.InvariantCulture)));
            return set;
        }

        /// <summary>The runs in all (the progress total).</summary>
        public int RunCount => Scenarios.Sum(s => Math.Max(1, s.Replications));

        // ── as a file ────────────────────────────────────────────────────────

        public static string Serialize(StudyBatch batch) => Serialize(batch, null);

        /// <summary>The batch envelope; with <paramref name="id"/> (a request to the runner's session, <c>simulean serve</c>).</summary>
        public static string Serialize(StudyBatch batch, string id)
        {
            var sb = new StringBuilder(8192);
            sb.Append("{\"format\":").Append(ConfigJson.FormatVersion).Append(",\"kind\":\"batch\",");
            if (id != null) sb.Append("\"id\":\"").Append(id).Append("\",");
            sb.Append("\"batch\":");
            ConfigJson.Write(sb, batch ?? throw new ArgumentNullException(nameof(batch)), typeof(StudyBatch), "batch");
            sb.Append('}');
            return sb.ToString();
        }

        public static StudyBatch Deserialize(string json)
        {
            if (!(MiniJson.Parse(json ?? throw new ArgumentNullException(nameof(json))) is Dictionary<string, object> root))
                throw new FormatException("A batch file is a JSON object {\"format\": 1, \"kind\": \"batch\", \"batch\": {…}}.");
            return FromEnvelope(root);
        }

        public static StudyBatch FromEnvelope(Dictionary<string, object> root)
        {
            ConfigJson.CheckEnvelope(root, "batch");
            if (!(root.TryGetValue("batch", out object b) && b is Dictionary<string, object>))
                throw new FormatException("The batch file has no \"batch\" object.");
            return (StudyBatch)ConfigJson.Read(b, typeof(StudyBatch), "batch");
        }
    }
}
