using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Engine-pure config codec and headless build of a <see cref="LengthLimitedQueue"/>
    /// (the <c>UnityLengthLimitedQueueHandler</c> only extracts the Unity fields and delegates here,
    /// so the exact build path can be tested without Unity). Parameter keys in
    /// <see cref="ElementConfig.Parameters"/>:
    /// <list type="bullet">
    /// <item><c>laneLengths</c> — <c>List&lt;string&gt;</c>, one invariant-culture number per lane
    ///   (a <c>List&lt;string&gt;</c> so the GA's <c>CloneConfig</c> deep-clones it);</item>
    /// <item><c>gap</c>, <c>defaultItemLength</c> (double), <c>lengthLabel</c> (string),
    ///   <c>maxItems</c> (int, ≤ 0 = unlimited), <c>acceptOversize</c> (bool);</item>
    /// <item>the standard <c>inputStrategy*</c> / <c>outputStrategy*</c> keys.</item>
    /// </list>
    /// Values may arrive as int / float / double / string depending on the extraction path;
    /// reads are tolerant.
    /// </summary>
    public static class LengthLimitedQueueBuilder
    {
        public const string KeyLaneLengths       = "laneLengths";
        public const string KeyGap               = "gap";
        public const string KeyDefaultItemLength = "defaultItemLength";
        public const string KeyLengthLabel       = "lengthLabel";
        public const string KeyMaxItems          = "maxItems";
        public const string KeyAcceptOversize    = "acceptOversize";

        /// <summary>Writes the lane parameters into <paramref name="config"/>.</summary>
        public static void Encode(ElementConfig config, IEnumerable<double> laneLengths, double gap,
                                  double defaultItemLength, string lengthLabel, int maxItems, bool acceptOversize)
        {
            var lanes = new List<string>();
            if (laneLengths != null)
                foreach (double l in laneLengths) lanes.Add(l.ToString("R", CultureInfo.InvariantCulture));
            config.Parameters[KeyLaneLengths]       = lanes;
            config.Parameters[KeyGap]               = gap;
            config.Parameters[KeyDefaultItemLength] = defaultItemLength;
            config.Parameters[KeyLengthLabel]       = lengthLabel ?? "";
            config.Parameters[KeyMaxItems]          = maxItems;
            config.Parameters[KeyAcceptOversize]    = acceptOversize;
        }

        /// <summary>Builds the element (input/output strategies included) from <paramref name="config"/>.</summary>
        public static LengthLimitedQueue Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var lanes = ReadLaneLengths(config);
            var queue = new LengthLimitedQueue(config.Name, clock, lanes.ToArray(),
                                               ElementBuildHelpers.BuildInputStrategy(config), vElement)
            {
                Gap                       = GetDouble(config, KeyGap, 0.2),
                DefaultItemLength         = GetDouble(config, KeyDefaultItemLength, 1.0),
                LengthLabel               = GetString(config, KeyLengthLabel, "length"),
                AcceptOversizeOnEmptyLane = GetBool(config, KeyAcceptOversize, true),
            };
            int max = (int)GetDouble(config, KeyMaxItems, 0);
            queue.MaxItems = max > 0 ? max : int.MaxValue;
            ElementBuildHelpers.ApplyOutputStrategy(queue, config);
            return queue;
        }

        /// <summary>
        /// Lane lengths from the config (default one lane of 10 units): a list of texts (a decimal comma accepted), a
        /// list of numbers (also as loaded from JSON), or one text separated by ';' or spaces (',' too when no lane uses a
        /// decimal comma). A length that is not a positive number fails, naming it (invalid entries were skipped and a
        /// JSON list fell back to one lane of 10).
        /// </summary>
        public static List<double> ReadLaneLengths(ElementConfig config)
        {
            var result = new List<double>();
            if (config.Parameters != null && config.Parameters.TryGetValue(KeyLaneLengths, out object raw) && raw != null)
            {
                IEnumerable<object> entries;
                if (raw is string csv)
                {
                    char[] seps = csv.IndexOf(';') >= 0 ? new[] { ';', ' ' } : new[] { ',', ';', ' ' };
                    entries = csv.Split(seps, StringSplitOptions.RemoveEmptyEntries);
                }
                else if (raw is System.Collections.IEnumerable list) entries = list.Cast<object>();
                else entries = new[] { raw };
                foreach (object e in entries)
                {
                    double v;
                    if (e is string t) { if (string.IsNullOrWhiteSpace(t)) continue; if (!SimuLean.Data.ModelTable.TryNumber(t, out v)) v = double.NaN; }
                    else if (e is IConvertible) v = Convert.ToDouble(e, CultureInfo.InvariantCulture);
                    else v = double.NaN;
                    if (!(v > 0))
                        throw new FormatException($"'{config.Name}': lane length '{e}' is not a positive number.");
                    result.Add(v);
                }
            }
            if (result.Count == 0) result.Add(10.0);
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
