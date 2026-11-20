using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Headless;
using SimuLean.Serialization;

namespace SimuLean.Logic
{
    public static partial class ModelLogic
    {
        // ── run-time counters ───────────────────────────────

        /// <summary>
        /// Attaches to <paramref name="report"/>'s decisions the run-time counters of <paramref name="model"/> — the model built
        /// from the same config, running or run — and returns the report: a routing's items per output, its last choice (with
        /// the scores of a best-score or rule routing and the item's labels) and the items it held for a full best output; an
        /// admission's entries, checks, refusals and wake-ups; a Queue's releases and the last item released (with its labels); a team's requests served; a reaction's
        /// fires and the signals whose condition was false; a choice rule's calls, errors and last score
        /// (<see cref="Decision.Live"/>, <see cref="Decision.LiveText"/>). Read-only: it reads counters the model keeps anyway
        /// (<see cref="DecisionCounters"/>, <see cref="SimuLean.Signals.Reaction.Fired"/>, <see cref="SimuLean.Resources.ResourceDispatcher.Served"/>,
        /// <see cref="SimuLean.Rules.RuleSiteBase"/>), so reading them never changes a result. <paramref name="only"/> = that owner only.
        /// </summary>
        public static LogicReport Live(LogicReport report, HeadlessModelFactory model, string only = null)
        {
            if (report == null || model == null) return report;
            foreach (var o in report.Owners)
            {
                if (only != null && o.Name != only) continue;
                foreach (var d in o.Decisions) { d.Live = null; d.LiveText = null; }
                switch (o.Kind)
                {
                    case "element":
                        var e = (o.Id != null ? model.GetElement(o.Id) : null) ?? FindQuietly(model, o.Name);
                        if (e != null) LiveElement(o, e);
                        break;
                    case "team":
                        if (o.Id != null && model.Teams.TryGetValue(o.Id, out var team))
                            foreach (var d in o.Decisions.Where(x => x.Site == Name(LogicSite.Dispatch)))
                            {
                                Put(d, "served", (long)team.Served, $"served {team.Served}");
                                if (team.RequestRule != null) RuleLive(d, team.RequestRule);
                            }
                        break;
                    case "reaction":
                        if (o.Id != null && model.ReactionsById.TryGetValue(o.Id, out var r))
                            foreach (var d in o.Decisions)
                            {
                                Put(d, "fired", (long)r.Fired, $"fired {r.Fired}");
                                if (r.Condition != null) Put(d, "notMet", (long)r.Skipped, $"condition false {r.Skipped}");
                            }
                        break;
                }
            }
            return report;
        }

        private static Element FindQuietly(HeadlessModelFactory model, string name)
        {
            try { return model.Find(name); }
            catch (InvalidOperationException) { return null; }   // two of that name: the id decides
        }

        private static void LiveElement(LogicOwner o, Element e)
        {
            var c = e.Decisions;
            foreach (var d in o.Decisions)
            {
                bool routing = d.Site == Name(LogicSite.Routing) && d.Verb != Decision.VerbPush && d.Sites.Any(s => s.Key.StartsWith("outputStrategy.", StringComparison.Ordinal));
                bool admission = d.Site == Name(LogicSite.Admission) && d.Sites.Any(s => s.Key.StartsWith("inputStrategy.", StringComparison.Ordinal));
                bool release = d.Site == Name(LogicSite.Order) && e is ItemsQueue && d.Sites.Any(s => s.Key == "offerOrder" || s.Key == "offerSort" || s.Key == "offerRule");
                if (routing)
                {
                    var counts = new Dictionary<string, object>();
                    if (c?.Outputs != null)
                        for (int i = 0; i < c.Outputs.Count; i++) counts[c.Outputs[i].Name] = c.RoutedTo(i);
                    Put(d, "routed", counts, "routed " + (counts.Count == 0 ? "0" : string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}"))));
                    if (c?.LastOutput != null)
                    {
                        string scores = Scores(c);
                        Put(d, "last", c.LastOutput.Name, $"last → {c.LastOutput.Name}" + (scores != null ? $" ({scores})" : "") + ForItem(c.LastItem));
                        if (scores != null)
                        {
                            d.Live["lastScores"] = c.LastScores.Select(x => double.IsNaN(x) ? (object)null : x).ToList();
                            d.Live["lastSkipped"] = Skipped(c);
                        }
                        PutItem(d, "lastItem", c.LastItem);
                    }
                    if (c != null && c.HeldForBest > 0 || d.Sites.Any(s => s.Key.EndsWith("whenBestFull", StringComparison.Ordinal) && s.Value == "Wait"))
                        Put(d, "heldForBest", c?.HeldForBest ?? 0, $"held for the full best {c?.HeldForBest ?? 0}");
                    if (e.GetOutputStrategy() is SimuLean.Rules.RuleSiteBase rule) RuleLive(d, rule, c?.LastItem);
                }
                else if (admission)
                {
                    // entries are items; checks are evaluations — an item may be checked several times before it enters
                    Put(d, "entered", c?.Entered ?? 0, $"entered {c?.Entered ?? 0}");
                    Put(d, "refused", c?.Refused ?? 0, $"refused {c?.Refused ?? 0} of {c?.Checks ?? 0} checks");
                    Put(d, "checks", c?.Checks ?? 0, null);
                    Put(d, "wakeUps", c?.WakeUps ?? 0, o.WokenBy.Count > 0 || (c?.WakeUps ?? 0) > 0 ? $"woken {c?.WakeUps ?? 0}" : null);
                }
                else if (release)
                {
                    // every item that left, those the order chose (after waiting) and those that passed straight through
                    long waited = c?.Released ?? 0, passed = c?.PassedThrough ?? 0;
                    Put(d, "left", waited + passed, $"left {waited + passed}");
                    Put(d, "released", waited, $"{waited} after waiting (ordered)");
                    Put(d, "passedThrough", passed, $"{passed} straight through");
                    if (c?.LastReleased != null)
                    {
                        Put(d, "lastReleased", ItemText(c.LastReleased), $"last {ItemText(c.LastReleased)}{LabelsText(c.LastReleased)} at {Num(c.LastReleasedAt)}");
                        PutItem(d, "lastReleasedItem", c.LastReleased);
                    }
                    if (((ItemsQueue)e).OfferSort is SimuLean.Rules.RuleSiteBase rule) RuleLive(d, rule, c?.LastReleased);
                }
            }
        }

        // the chosen output's score first, then the others in connection order: 10 vs -2, -1
        private static string Scores(DecisionCounters c)
        {
            if (c.LastScored < 0 || c.LastScored >= c.LastScores.Count || c.Outputs == null || c.LastScores.Count != c.Outputs.Count) return null;
            if (!ReferenceEquals(c.Outputs[c.LastScored], c.LastOutput)) return null;   // the last choice was not scored
            var others = c.LastScores.Where((x, i) => i != c.LastScored && !double.IsNaN(x)).Select(Short).ToList();
            var skipped = Skipped(c);
            return Short(c.LastScores[c.LastScored]) + (others.Count > 0 ? " vs " + string.Join(", ", others) : "")
                   + (skipped.Count > 0 ? "; skipped " + string.Join(", ", skipped) + " (full, refusing or stopped)" : "");
        }

        // the outputs the last scored choice did not score — full, refusing the item or stopped (whenBestFull = Skip, or an
        // admission saying no)
        private static List<string> Skipped(DecisionCounters c)
            => c.LastScores.Select((x, i) => (x, i)).Where(p => double.IsNaN(p.x) && p.i < c.Outputs.Count).Select(p => c.Outputs[p.i].Name).ToList();

        private static string Short(double x) => Math.Round(x, 3).ToString("0.###", CultureInfo.InvariantCulture);

        private static string ItemText(Item item) => $"item {item.GetId()}" + (string.IsNullOrEmpty(item.Name) ? "" : $" ({item.Name})");

        /// <summary>The labels shown with a last decision: the first few, so a line stays one line.</summary>
        internal const int LabelsShown = 4;

        // " for item 12 {family=3, prio=2}" — the item of a last decision and its labels (as they are when read)
        private static string ForItem(Item item) => item == null ? "" : $" for {ItemText(item)}{LabelsText(item)}";

        /// <summary>The item's size labels (<see cref="Item.SetSizeWhereMissing"/>: its 3D model's width, height, length and
        /// load floor), left out of a decision's labels (<c>width</c> / <c>height</c> would fill the four labels shown
        /// in <c>simulean_diagnose</c>); the data form gives them apart, as <c>size</c>.</summary>
        public static readonly IReadOnlyList<string> SizeLabels = new[]
        {
            SimuLean.Storage.StorageSystem.DefaultWidthLabel, SimuLean.Storage.StorageSystem.DefaultHeightLabel,
            SimuLean.Storage.StorageSystem.DefaultLengthLabel, SimuLean.Storage.StorageSystem.DefaultLoadFloorLabel,
        };

        private static bool IsSize(string label) => SizeLabels.Contains(label);

        // the labels a decision shows: the model's, not the size of the item's 3D model
        private static List<KeyValuePair<string, object>> DecisionLabels(Item item)
            => item.Labels.Where(kv => !IsSize(kv.Key)).ToList();

        // " {family=3, prio=2}" (the first LabelsShown, "…" when there are more), "" without labels
        private static string LabelsText(Item item)
        {
            if (item == null || !item.HasProperties) return "";
            var labels = DecisionLabels(item);
            if (labels.Count == 0) return "";
            var shown = labels.Take(LabelsShown).Select(kv => kv.Key + "=" + LabelValue(kv.Value)).ToList();
            return " {" + string.Join(", ", shown) + (labels.Count > LabelsShown ? ", …" : "") + "}";
        }

        private static string LabelValue(object v) => v is double x ? Short(x) : v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString() ?? "";

        // the data form: { id, name?, labels { first LabelsShown } }
        private static void PutItem(Decision d, string key, Item item)
        {
            if (item == null) return;
            var data = new Dictionary<string, object> { ["id"] = item.GetId() };
            if (!string.IsNullOrEmpty(item.Name)) data["name"] = item.Name;
            if (item.HasProperties)
            {
                var labels = DecisionLabels(item);
                if (labels.Count > 0)
                    data["labels"] = labels.Take(LabelsShown).ToDictionary(kv => kv.Key, kv => kv.Value is double x ? (object)Math.Round(x, 3) : kv.Value);
                var size = item.Labels.Where(kv => IsSize(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value is double x ? (object)Math.Round(x, 3) : kv.Value);
                if (size.Count > 0) data["size"] = size;
            }
            if (d.Live == null) d.Live = new Dictionary<string, object>();
            d.Live[key] = data;
        }

        // shown = the item the decision's line already names (not repeated)
        private static void RuleLive(Decision d, SimuLean.Rules.RuleSiteBase rule, Item shown = null)
        {
            Put(d, "ruleCalls", rule.Decisions, $"rule calls {rule.Decisions}");
            Put(d, "ruleErrors", rule.Errors, rule.Errors > 0 ? $"errors {rule.Errors}" : null);
            if (!double.IsNaN(rule.LastScore))
            {
                Put(d, "lastScore", rule.LastScore, $"last score {Short(rule.LastScore)}{(ReferenceEquals(rule.LastItem, shown) ? "" : ForItem(rule.LastItem))}");
                PutItem(d, "lastScoreItem", rule.LastItem);
            }
        }

        // a counter into the decision's live data, and its words into its live text
        private static void Put(Decision d, string key, object value, string text)
        {
            if (d.Live == null) d.Live = new Dictionary<string, object>();
            d.Live[key] = value;
            if (!string.IsNullOrEmpty(text)) d.LiveText = d.LiveText == null ? text : d.LiveText + " · " + text;
        }
    }
}
