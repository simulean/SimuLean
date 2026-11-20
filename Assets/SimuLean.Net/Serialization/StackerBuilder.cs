using System;
using System.Collections.Generic;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Headless build of a <see cref="Stacker"/> from its typed settings (<see cref="StackerSettings"/>); the
    /// <c>StackerHandler</c> delegates here, so the exact build path is testable without Unity. The cycle time is a sampler
    /// spec with its own stream (purpose <c>cycleTime</c>).
    /// </summary>
    public static class StackerBuilder
    {
        /// <summary>Builds the element (input/output strategies and cycle-time sampler included) from <paramref name="config"/>.</summary>
        public static Stacker Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<StackerSettings>();
            int n = Math.Max(1, s.PositionCount);
            int cap = Math.Max(1, s.Capacity);
            var stacker = new Stacker(config.Name, clock, n, cap, ElementBuildHelpers.BuildInputStrategy(config), vElement)
            {
                MaxHeight = s.MaxHeight,
                HeightLabel = s.HeightLabel ?? "",
                DefaultItemHeight = s.DefaultItemHeight,
                AcceptOversizeOnEmpty = s.AcceptOversizeOnEmpty,
                SelectionRule = s.SelectionRule,
                PositionLabel = s.PositionLabel ?? "",
                KeyLabel = s.KeyLabel ?? "",
                ReleasePartialWhenBlocked = s.ReleasePartialWhenBlocked,
                StackType = s.StackType ?? "",
                CopyLabelsFromFirst = s.CopyLabelsFromFirst,
            };
            var caps = s.PositionCapacities ?? new int[0];
            for (int i = 0; i < caps.Length && i < n; i++) stacker.Positions[i].Capacity = caps[i];
            if (!string.IsNullOrEmpty(s.PickNode))
            {
                stacker.PickLocation = s.PickNode;
                var locs = s.PositionNodes ?? new List<string>();
                for (int i = 0; i < locs.Count && i < n; i++) stacker.Positions[i].Location = string.IsNullOrEmpty(locs[i]) ? null : locs[i];
            }

            if (s.CycleTime != null) stacker.CycleTime = ElementBuildHelpers.SamplerOf(s.CycleTime, clock, config.Name, "cycleTime", "cycleTime");

            ElementBuildHelpers.ApplyOutputStrategy(stacker, config);
            return stacker;
        }
    }
}
