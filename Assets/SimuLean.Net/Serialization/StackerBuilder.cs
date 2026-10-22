using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Engine-pure config codec and headless build of a <see cref="Stacker"/> (the
    /// <c>UnityStackerHandler</c> only extracts the Unity fields and delegates here, so the exact build
    /// path is testable without Unity). Parameter keys in <see cref="ElementConfig.Parameters"/>:
    /// <list type="bullet">
    /// <item><c>positions</c> (int ≥ 1), <c>capacity</c> (int ≥ 1);</item>
    /// <item><c>positionCapacities</c> — optional <c>List&lt;string&gt;</c>, one invariant integer per position
    ///   (≤ 0 = the default capacity; a <c>List&lt;string&gt;</c> so the GA's <c>CloneConfig</c> deep-clones it);</item>
    /// <item><c>maxHeight</c>, <c>defaultItemHeight</c> (double), <c>heightLabel</c> (string),
    ///   <c>acceptOversize</c> (bool);</item>
    /// <item><c>selectionRule</c> (<see cref="StackSelectionRule"/> name), <c>positionLabel</c>, <c>keyLabel</c>
    ///   (string), <c>releasePartialWhenBlocked</c> (bool);</item>
    /// <item><c>cycleTime</c> — compact <see cref="SamplerSpec"/> (<c>Type~p1~p2</c>; empty = instantaneous),
    ///   rebuilt with its own per-model random stream;</item>
    /// <item><c>stackType</c> (string), <c>copyLabels</c> (bool);</item>
    /// <item>the standard <c>inputStrategy*</c> / <c>outputStrategy*</c> keys.</item>
    /// </list>
    /// Values may arrive as int / float / double / string depending on the extraction path; reads are tolerant.
    /// </summary>
    public static class StackerBuilder
    {
        public const string KeyPositions = "positions";
        public const string KeyCapacity = "capacity";
        public const string KeyPositionCapacities = "positionCapacities";
        public const string KeyMaxHeight = "maxHeight";
        public const string KeyHeightLabel = "heightLabel";
        public const string KeyDefaultItemHeight = "defaultItemHeight";
        public const string KeyAcceptOversize = "acceptOversize";
        public const string KeySelectionRule = "selectionRule";
        public const string KeyPositionLabel = "positionLabel";
        public const string KeyKeyLabel = "keyLabel";
        public const string KeyReleasePartial = "releasePartialWhenBlocked";
        public const string KeyCycleTime = "cycleTime";
        public const string KeyStackType = "stackType";
        public const string KeyCopyLabels = "copyLabels";
        public const string KeyPickLocation = "pickLocation";
        public const string KeyPositionLocations = "positionLocations";

        /// <summary>Plain settings of a stacker (what <see cref="Encode"/> writes).</summary>
        public sealed class Settings
        {
            public int Positions = 2;
            public int Capacity = 10;
            public IList<int> PositionCapacities;
            public double MaxHeight;
            public string HeightLabel = "height";
            public double DefaultItemHeight;
            public bool AcceptOversize = true;
            public StackSelectionRule SelectionRule = StackSelectionRule.FirstNotFull;
            public string PositionLabel = Stacker.LabelPosition;
            public string KeyLabel = "";
            public bool ReleasePartialWhenBlocked = true;
            /// <summary>Compact sampler spec (empty/null = instantaneous).</summary>
            public string CycleTime = "";
            public string StackType = "Stack";
            public bool CopyLabels = true;
            /// <summary>Crane mode: pick location and one location per position (null = cycle by time).</summary>
            public string PickLocation;
            public IList<string> PositionLocations;
        }

        /// <summary>Writes the stacker parameters into <paramref name="config"/>.</summary>
        public static void Encode(ElementConfig config, Settings s)
        {
            var caps = new List<string>();
            if (s.PositionCapacities != null)
                foreach (int c in s.PositionCapacities) caps.Add(c.ToString(CultureInfo.InvariantCulture));
            config.Parameters[KeyPositions] = Math.Max(1, s.Positions);
            config.Parameters[KeyCapacity] = Math.Max(1, s.Capacity);
            config.Parameters[KeyPositionCapacities] = caps;
            config.Parameters[KeyMaxHeight] = s.MaxHeight;
            config.Parameters[KeyHeightLabel] = s.HeightLabel ?? "";
            config.Parameters[KeyDefaultItemHeight] = s.DefaultItemHeight;
            config.Parameters[KeyAcceptOversize] = s.AcceptOversize;
            config.Parameters[KeySelectionRule] = s.SelectionRule.ToString();
            config.Parameters[KeyPositionLabel] = s.PositionLabel ?? "";
            config.Parameters[KeyKeyLabel] = s.KeyLabel ?? "";
            config.Parameters[KeyReleasePartial] = s.ReleasePartialWhenBlocked;
            config.Parameters[KeyCycleTime] = s.CycleTime ?? "";
            config.Parameters[KeyStackType] = s.StackType ?? "Stack";
            config.Parameters[KeyCopyLabels] = s.CopyLabels;
            if (!string.IsNullOrEmpty(s.PickLocation))
            {
                config.Parameters[KeyPickLocation] = s.PickLocation;
                config.Parameters[KeyPositionLocations] = s.PositionLocations != null ? new List<string>(s.PositionLocations) : new List<string>();
            }
        }

        /// <summary>Builds the element (input/output strategies and cycle-time sampler included) from <paramref name="config"/>.</summary>
        public static Stacker Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            int n = Math.Max(1, (int)GetDouble(config, KeyPositions, 2));
            int cap = Math.Max(1, (int)GetDouble(config, KeyCapacity, 10));
            var stacker = new Stacker(config.Name, clock, n, cap, ElementBuildHelpers.BuildInputStrategy(config), vElement)
            {
                MaxHeight = GetDouble(config, KeyMaxHeight, 0),
                HeightLabel = GetString(config, KeyHeightLabel, "height"),
                DefaultItemHeight = GetDouble(config, KeyDefaultItemHeight, 0),
                AcceptOversizeOnEmpty = GetBool(config, KeyAcceptOversize, true),
                SelectionRule = ParseRule(GetString(config, KeySelectionRule, "FirstNotFull")),
                PositionLabel = GetString(config, KeyPositionLabel, Stacker.LabelPosition),
                KeyLabel = GetString(config, KeyKeyLabel, ""),
                ReleasePartialWhenBlocked = GetBool(config, KeyReleasePartial, true),
                StackType = GetString(config, KeyStackType, "Stack"),
                CopyLabelsFromFirst = GetBool(config, KeyCopyLabels, true),
            };
            var caps = ReadPositionCapacities(config);
            for (int i = 0; i < caps.Count && i < n; i++) stacker.Positions[i].Capacity = caps[i];
            string pick = GetString(config, KeyPickLocation, "");
            if (!string.IsNullOrEmpty(pick))
            {
                stacker.PickLocation = pick;
                var locs = config.GetParameter<List<string>>(KeyPositionLocations, null) ?? new List<string>();
                for (int i = 0; i < locs.Count && i < n; i++) stacker.Positions[i].Location = string.IsNullOrEmpty(locs[i]) ? null : locs[i];
            }

            string spec = GetString(config, KeyCycleTime, "");
            if (!string.IsNullOrWhiteSpace(spec)) stacker.CycleTime = SamplerSpec.Decode(spec.Trim(), clock, config.Name, "cycleTime");

            ElementBuildHelpers.ApplyOutputStrategy(stacker, config);
            return stacker;
        }

        /// <summary>Selection rule by name (case-insensitive; unknown = <see cref="StackSelectionRule.FirstNotFull"/>).</summary>
        public static StackSelectionRule ParseRule(string s)
            => Enum.TryParse(s ?? "", true, out StackSelectionRule r) ? r : StackSelectionRule.FirstNotFull;

        /// <summary>Per-position capacities from the config (missing = empty list).</summary>
        public static List<int> ReadPositionCapacities(ElementConfig config)
        {
            var result = new List<int>();
            if (config.Parameters == null || !config.Parameters.TryGetValue(KeyPositionCapacities, out object raw) || raw == null)
                return result;
            if (raw is IEnumerable<string> texts)
            {
                foreach (string t in texts)
                    result.Add(int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0);
            }
            else if (raw is IEnumerable<int> ints) result.AddRange(ints);
            else if (raw is string csv)
            {
                foreach (string t in csv.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    result.Add(int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0);
            }
            return result;
        }

        private static double GetDouble(ElementConfig c, string key, double def)
        {
            if (c.Parameters == null || !c.Parameters.TryGetValue(key, out object raw) || raw == null) return def;
            if (raw is string s)
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
            try { return Convert.ToDouble(raw, CultureInfo.InvariantCulture); } catch { return def; }
        }

        private static string GetString(ElementConfig c, string key, string def)
            => c.Parameters != null && c.Parameters.TryGetValue(key, out object raw) && raw != null
                ? Convert.ToString(raw, CultureInfo.InvariantCulture) : def;

        private static bool GetBool(ElementConfig c, string key, bool def)
        {
            if (c.Parameters == null || !c.Parameters.TryGetValue(key, out object raw) || raw == null) return def;
            if (raw is bool b) return b;
            return bool.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out bool r) ? r : def;
        }
    }
}
