using System;
using System.Collections.Generic;
using System.Linq;

namespace SimuLean.Serialization
{
    /// <summary>
    /// "Did you mean" for a name that is not known: the closest options by edit distance, ignoring case,
    /// so an error names what was probably meant instead of only saying "unknown". Used by the study checks and the MCP
    /// commands.
    /// </summary>
    public static class NameSuggestions
    {
        /// <summary>Up to <paramref name="max"/> options closest to <paramref name="given"/> (an option containing it, or
        /// within about a third of its length in edits), the closest first; empty when none is close.</summary>
        public static List<string> Closest(string given, IEnumerable<string> options, int max = 3)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(given) || options == null) return result;
            string g = given.ToLowerInvariant();
            int limit = Math.Max(2, g.Length / 3);
            var scored = new List<(string option, int score)>();
            foreach (string o in options.Where(o => !string.IsNullOrEmpty(o)).Distinct())
            {
                string l = o.ToLowerInvariant();
                int d = l == g ? 0 : l.Contains(g) || g.Contains(l) ? 1 : Distance(g, l);
                if (d <= limit) scored.Add((o, d));
            }
            foreach (var (o, _) in scored.OrderBy(s => s.score).ThenBy(s => s.option, StringComparer.Ordinal).Take(max)) result.Add(o);
            return result;
        }

        /// <summary>" Did you mean 'a' or 'b'?" — or "" when nothing is close.</summary>
        public static string DidYouMean(string given, IEnumerable<string> options)
        {
            var c = Closest(given, options);
            return c.Count == 0 ? "" : " Did you mean " + string.Join(" or ", c.Select(x => "'" + x + "'")) + "?";
        }

        /// <summary>Levenshtein distance.</summary>
        public static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
