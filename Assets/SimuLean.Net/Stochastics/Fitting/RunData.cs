using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Stochastics.Fitting
{
    /// <summary>What to collect from a run for fitting.</summary>
    public enum RunQuantity
    {
        /// <summary>Time each item spends in the element (process time of a station, wait in a buffer).</summary>
        StayTime,
        /// <summary>Time between two consecutive arrivals at the element.</summary>
        InterArrival,
        /// <summary>Time between two consecutive departures from the element.</summary>
        InterDeparture,
    }

    /// <summary>
    /// Times recorded in a run of a model ("times of a run"): runs the model headless with the element's raw
    /// samples on (<see cref="Statistics.ElementStatsCollector.RecordSamples"/>) and returns the stay times or the gaps
    /// between arrivals / departures — e.g. to compare what the model produces with the real data, or to fit an
    /// aggregated distribution to a detailed sub-model.
    /// </summary>
    public static class RunData
    {
        public static List<double> Collect(SimulationConfig config, string elementName, RunQuantity quantity, double runTime, int seed)
        {
            var clock = new SimClock(seed);
            var model = new HeadlessModelFactory(clock);
            model.BuildModel(config);
            var element = model.GetAllElements().Values.FirstOrDefault(e => e.GetName() == elementName)
                          ?? throw new ArgumentException($"No element '{elementName}' in the model.");
            var stats = element.StatsCollector ?? throw new InvalidOperationException($"'{elementName}' has no statistics.");
            stats.RecordSamples();
            model.StartModel();
            clock.AdvanceClock(runTime > 0 ? runTime : config.MaxSimulationTime > 0 ? config.MaxSimulationTime : 100);
            // read through the run's results, like every other statistic
            var samples = RunResults.Capture(model).Samples[elementName];
            switch (quantity)
            {
                case RunQuantity.StayTime: return samples.StayTimes;
                case RunQuantity.InterArrival: return Gaps(samples.EntryTimes);
                default: return Gaps(samples.ExitTimes);
            }
        }

        private static List<double> Gaps(List<double> times)
        {
            var gaps = new List<double>(Math.Max(0, times.Count - 1));
            for (int i = 1; i < times.Count; i++) gaps.Add(times[i] - times[i - 1]);
            return gaps;
        }
    }
}
