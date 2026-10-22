using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>
    /// The whole model in one compact, JSON-friendly tree (<c>simulean_model</c>): the canonical model — the
    /// <see cref="SimulationConfig"/> a run builds — rather than the scene, with everything named by name (not by id) and
    /// every parameter under the key a study varies (<c>Owner.key</c>, <c>team:T.key</c>, <c>downtime:id.key</c>,
    /// <c>reaction:id.key</c>): the clock (time unit, horizon, warm-up, end condition, seed), the elements (type, next
    /// elements in order, parameters), the other connections (lists, ports), the teams (members with their labels,
    /// parameters), lists, downtimes, reactions and data tables. Read-only.
    /// </summary>
    public static class ModelDescription
    {
        /// <param name="parameters">Include every element's / team's parameters (false: the structure only).</param>
        /// <param name="only">Describe only the element / team / list / downtime / reaction of this name (null = all).</param>
        public static Dictionary<string, object> Of(SimulationConfig model, bool parameters = true, string only = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in model.Elements) if (e.Id != null) names[e.Id] = e.Name ?? e.Id;
            foreach (var l in model.Lists) if (l.Id != null) names[l.Id] = l.Name ?? l.Id;
            string NameOf(string id) => id != null && names.TryGetValue(id, out var n) ? n : id;
            bool Wanted(string name) => only == null || string.Equals(name, only, StringComparison.Ordinal);

            var d = new Dictionary<string, object>();
            double unit = model.TimeUnitSeconds > 0 ? model.TimeUnitSeconds : 1.0;
            if (only == null)
            {
                string end = null;
                if (model.StopAt != null && model.StopAt.Count > 0 && model.StopAtCount > 0)
                    end = $"{model.StopAtCount} items received by {string.Join(", ", model.StopAt.Select(NameOf))}";
                if (model.StopWhenEmpty) end = (end != null ? end + "; " : "") + "nothing left to do (or a deadlock / stall)";
                d["clock"] = new Dictionary<string, object>
                {
                    ["timeUnit"] = ResultMetrics.TimeUnitName(unit),
                    ["secondsPerTimeUnit"] = unit,
                    ["horizon"] = model.MaxSimulationTime,
                    ["warmUp"] = model.WarmupTime,
                    ["start"] = string.IsNullOrWhiteSpace(model.StartDateTime) ? null : model.StartDateTime,
                    ["endCondition"] = end,
                    ["seed"] = model.UseFixedSeed ? (object)model.Seed : "per run",
                    ["holidays"] = model.Holidays != null && model.Holidays.Count > 0 ? model.Holidays : null,
                };
            }

            // flow connections in order (index routing reads it): the element's "next"; the others listed apart
            var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var other = new List<string>();
            foreach (var c in model.Connections)
            {
                if (c.ConnectionType == "General")
                {
                    if (!next.TryGetValue(c.SourceId ?? "", out var list)) next[c.SourceId ?? ""] = list = new List<string>();
                    list.Add(NameOf(c.TargetId));
                }
                else if (only == null || Wanted(NameOf(c.SourceId)) || Wanted(NameOf(c.TargetId)))
                    other.Add($"{NameOf(c.SourceId)} -> {NameOf(c.TargetId)} ({c.ConnectionType}{Extra(c.Parameters)})");
            }

            d["elements"] = model.Elements.Where(e => Wanted(e.Name)).Select(e =>
            {
                var x = new Dictionary<string, object>
                {
                    ["name"] = e.Name,
                    ["type"] = TypeName(e.Type),
                    ["next"] = next.TryGetValue(e.Id ?? "", out var n) ? n : new List<string>(),
                };
                if (parameters) x["parameters"] = Texts(e.Parameters);
                return x;
            }).ToList();
            if (other.Count > 0) d["connections"] = other;

            var teams = model.ResourceTeams.Where(t => Wanted(t.Name)).Select(t =>
            {
                var x = new Dictionary<string, object>
                {
                    ["name"] = t.Name,
                    ["members"] = t.Members.Select(m => m.Labels != null && m.Labels.Count > 0 ? $"{m.Name} [{string.Join(", ", m.Labels)}]" : m.Name).ToList(),
                };
                if (parameters) x["parameters"] = Catalog(ModelParameterCatalog.ForTeam(t));
                return x;
            }).ToList();
            if (teams.Count > 0) d["teams"] = teams;

            var lists = model.Lists.Where(l => Wanted(l.Name)).Select(l => new Dictionary<string, object>
            {
                ["name"] = l.Name,
                ["order"] = string.IsNullOrEmpty(l.DefaultOrderBy) ? "push order" : l.DefaultOrderBy + (l.DefaultDescending ? " desc" : ""),
                ["fields"] = l.Fields.Where(f => f != null).Select(f => string.IsNullOrEmpty(f.source) ? $"{f.name} ({f.kind})" : $"{f.name} ({f.kind}: {f.source})").ToList(),
            }).ToList();
            if (lists.Count > 0) d["lists"] = lists;

            var downtimes = model.Downtimes.Where(t => Wanted(t.Id)).Select(t =>
            {
                var x = new Dictionary<string, object>
                {
                    ["id"] = t.Id, ["kind"] = t.Kind, ["targets"] = t.TargetIds.Select(NameOf).ToList(),
                };
                if (parameters) x["parameters"] = Catalog(ModelParameterCatalog.ForDowntime(t));
                return x;
            }).ToList();
            if (downtimes.Count > 0) d["downtimes"] = downtimes;

            var reactions = model.Reactions.Where(r => Wanted(r.Id)).Select(r =>
            {
                var x = new Dictionary<string, object> { ["id"] = r.Id, ["sources"] = r.Sources.Select(NameOf).ToList() };
                if (parameters) x["parameters"] = Texts(r.Parameters);
                return x;
            }).ToList();
            if (reactions.Count > 0) d["reactions"] = reactions;

            var tables = model.Tables.Where(t => Wanted(t.Name)).Select(t => new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["source"] = t.Source == DataSourceKind.File ? "file " + t.File : t.Source == DataSourceKind.Sql ? "sql " + t.Sql : "inline",
                ["columns"] = t.Columns?.Keys.ToList(),
                ["rows"] = t.Columns == null || t.Columns.Count == 0 ? 0 : t.Columns.Values.First().Count,
            }).ToList();
            if (tables.Count > 0) d["tables"] = tables;
            if (only != null && ((IList)d["elements"]).Count == 0 && teams.Count == 0 && lists.Count == 0 && downtimes.Count == 0
                && reactions.Count == 0 && tables.Count == 0)
                throw new ArgumentException($"The model has nothing named '{only}'." + NameSuggestions.DidYouMean(only,
                    model.Elements.Select(e => e.Name).Concat(model.ResourceTeams.Select(t => t.Name)).Concat(model.Lists.Select(l => l.Name))
                         .Concat(model.Downtimes.Select(t => t.Id)).Concat(model.Reactions.Select(r => r.Id)).Concat(model.Tables.Select(t => t.Name))));
            return d;
        }

        /// <summary>"UnityMultiServer" → "MultiServer" (the Library's component type without the Unity prefix).</summary>
        private static string TypeName(string type)
            => type != null && type.StartsWith("Unity", StringComparison.Ordinal) && type.Length > 5 ? type.Substring(5) : type;

        private static string Extra(Dictionary<string, object> p)
            => p == null || p.Count == 0 ? "" : ": " + string.Join(", ", p.Select(kv => kv.Key + "=" + Text(kv.Value)));

        private static SortedDictionary<string, string> Texts(Dictionary<string, object> p)
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (p != null) foreach (var kv in p) d[kv.Key] = Text(kv.Value);
            return d;
        }

        private static SortedDictionary<string, string> Catalog(IEnumerable<ParameterInfo> list)
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in list) d[p.Key] = p.CurrentText;
            return d;
        }

        /// <summary>A config value as text (invariant numbers; lists in brackets; a nested table as its size).</summary>
        public static string Text(object v)
        {
            switch (v)
            {
                case null: return "";
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case double x: return x.ToString("R", CultureInfo.InvariantCulture);
                case float f: return ((double)f).ToString("R", CultureInfo.InvariantCulture);
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                case IDictionary dd: return $"({dd.Count} entries)";
                case IEnumerable e: return "[" + string.Join(", ", e.Cast<object>().Select(Text)) + "]";
                default: return v.ToString();
            }
        }
    }
}
