using System;
using System.Collections.Generic;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Headless build of a <see cref="LengthLimitedQueue"/> from its typed settings (<see cref="LengthLimitedQueueSettings"/>);
    /// the <c>LengthLimitedQueueHandler</c> delegates here, so the exact build path is tested without Unity.
    /// </summary>
    public static class LengthLimitedQueueBuilder
    {
        /// <summary>Builds the element (input/output strategies included) from <paramref name="config"/>.</summary>
        public static LengthLimitedQueue Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<LengthLimitedQueueSettings>();
            var lanes = LaneLengths(config.Name, s);
            var queue = new LengthLimitedQueue(config.Name, clock, lanes.ToArray(),
                                               ElementBuildHelpers.BuildInputStrategy(config), vElement)
            {
                Gap                       = s.Gap,
                DefaultItemLength         = s.DefaultItemLength,
                LengthLabel               = s.LengthLabel ?? "",
                AcceptOversizeOnEmptyLane = s.AcceptOversizeOnEmptyLane,
                Speed                     = s.Speed,
            };
            queue.MaxItems = s.MaxItems > 0 ? s.MaxItems : int.MaxValue;
            ElementBuildHelpers.ApplyOutputStrategy(queue, config);
            return queue;
        }

        /// <summary>The lane lengths (absent / empty = one lane of 10). A length that is not a positive number fails, naming
        /// it.</summary>
        public static List<double> LaneLengths(string owner, LengthLimitedQueueSettings s)
        {
            var result = new List<double>();
            foreach (double v in s.LaneLengths ?? new double[0])
            {
                if (!(v > 0)) throw new FormatException($"'{owner}': lane length '{v}' is not a positive number.");
                result.Add(v);
            }
            if (result.Count == 0) result.Add(10.0);
            return result;
        }
    }
}
