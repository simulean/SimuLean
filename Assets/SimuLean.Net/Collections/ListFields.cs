using System;
using System.Collections.Generic;
using System.Linq;

namespace SimuLean.Collections
{
    /// <summary>
    /// The field names list queries and order keys read, per kind of entry, and the build-time
    /// check of a query: a bare field the entries cannot have, or a root they do not serve, fails the build with a "did you
    /// mean". One name per value, as in expressions: <c>item.x</c> is the entry's item (its labels and built-ins
    /// <c>type</c>, <c>priority</c>, <c>id</c>, <c>age</c>, <c>created</c>), <c>origin.x</c> the element it comes from (its
    /// properties and built-ins <c>name</c>, <c>state</c>, <c>content</c>, <c>free</c>), <c>destination.x</c> where a carrier
    /// goes. A bare name is the entry's own field. In a list of items it may also be one of the item's labels (set during
    /// the run), so it is not checked there; a missing one warns once at run time.
    /// </summary>
    public static class ListFields
    {
        /// <summary>A team's request: the fields of <see cref="SimuLean.Resources.ResourceRequest"/>; <c>origin.x</c> = the
        /// requesting element.</summary>
        public static readonly IReadOnlyList<string> Request = new[] { "priority", "requestTime", "count", "oldestItemTime", "location", "taskType", "skill" };
        /// <summary>A team's member (<see cref="SimuLean.Resources.Resource"/>): these, and its properties by name.</summary>
        public static readonly IReadOnlyList<string> Member = new[] { "idleTime", "utilization", "tasks", "capacity", "distance", "name", "location" };
        /// <summary>A milk run's request (<see cref="SimuLean.Resources.MilkRunRequest"/>): these, its calculated request
        /// fields; <c>item.x</c>, <c>origin.x</c>, <c>destination.x</c>.</summary>
        public static readonly IReadOnlyList<string> MilkRunRequest = new[] { "distance", "onBoard", "originFill", "kind", "stop" };

        /// <summary>The roots each kind serves.</summary>
        public static readonly IReadOnlyList<string> ListRoots = new[] { "item", "origin" };
        public static readonly IReadOnlyList<string> RequestRoots = new[] { "origin" };
        public static readonly IReadOnlyList<string> MemberRoots = new string[0];
        public static readonly IReadOnlyList<string> MilkRunRoots = new[] { "item", "origin", "destination" };

        /// <summary>
        /// Checks the fields of <paramref name="query"/> (written as <paramref name="text"/> in <paramref name="where"/>):
        /// a root not in <paramref name="roots"/>, or — unless <paramref name="bareIsDynamic"/> — a bare name not in
        /// <paramref name="known"/>, throws <see cref="FormatException"/>.
        /// </summary>
        public static void Check(ListQuerySpec query, string text, IEnumerable<string> known, IReadOnlyList<string> roots,
                                 bool bareIsDynamic, string where)
        {
            if (query == null) return;
            var names = new List<string>();
            foreach (var c in query.conditions ?? new List<ListCondition>()) if (c != null && !string.IsNullOrEmpty(c.field)) names.Add(c.field);
            foreach (var k in query.OrderKeys()) names.Add(k.Key);
            var knownList = known?.ToList() ?? new List<string>();
            foreach (string f in names)
            {
                // a key / condition written as an expression: a list of items evaluates it over the entry (checked when
                // parsed and at build, as any expression); other entries (requests, members, milk-run requests) have no scope
                if (ListQuerySpec.IsExpression(f))
                {
                    if (bareIsDynamic) continue;
                    throw new FormatException($"{where}: '{f}' in '{text}' is an expression; here order keys and conditions are field names " +
                                              "(expressions in a list of items only)." + (knownList.Count > 0 ? $" Fields: {string.Join(", ", knownList)}." : ""));
                }
                int dot = f.IndexOf('.');
                if (dot > 0)
                {
                    string root = f.Substring(0, dot);
                    if (!roots.Contains(root))
                        throw new FormatException($"{where}: '{f}' in '{text}': '{root}.' is not a root of these entries" +
                                                  (roots.Count > 0 ? $" (roots: {string.Join(", ", roots)})." : " (they have no roots: use their fields)."));
                    continue;
                }
                if (bareIsDynamic || knownList.Contains(f)) continue;
                throw new FormatException($"{where}: unknown field '{f}' in '{text}'." +
                                          SimuLean.Serialization.NameSuggestions.DidYouMean(f, knownList) +
                                          $" Fields: {string.Join(", ", knownList)}" + (roots.Count > 0 ? $"; roots: {string.Join(", ", roots.Select(r => r + ".x"))}." : "."));
            }
        }
    }
}
