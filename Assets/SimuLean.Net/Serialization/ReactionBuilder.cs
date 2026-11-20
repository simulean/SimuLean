using System;
using System.Collections.Generic;
using SimuLean.Expressions;
using SimuLean.Signals;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Builds a <see cref="Reaction"/> from a <see cref="ReactionConfig"/> once the elements exist, and
    /// subscribes it to its sources. A malformed reaction fails the build naming it and the field: an unknown element,
    /// team, signal, action or list, an expression that does not parse, an item label on a signal without an item, a time
    /// signal with elements or without its time.
    /// </summary>
    public static class ReactionBuilder
    {
        public static Reaction Build(ReactionConfig cfg, IReadOnlyDictionary<string, Element> elements, SimClock clock,
                                     IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> teams = null)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            string name = string.IsNullOrWhiteSpace(cfg.Id) ? "Reaction" : cfg.Id;
            string Where(string what) => $"Reaction '{name}': {what}";

            var rs = cfg.Settings ?? new ReactionSettings();   // an unknown key or option failed when it was written
            var signal = rs.Signal;
            var reaction = new Reaction(name, signal, clock) { Enabled = rs.ReactionEnabled, Delivery = rs.Timing };
            if (!string.IsNullOrWhiteSpace(rs.State)) reaction.State = ElementState.Of(rs.State.Trim());
            string condition = rs.Condition;
            bool timeSignal = SignalKinds.IsTime(signal);
            var context = SignalKinds.HasItem(signal) ? ExpressionContext.ReactionItem
                        : timeSignal ? ExpressionContext.ReactionTime : ExpressionContext.ReactionElement;
            Expression Parse(string text, string at, bool asCondition = false)
            {
                try { return asCondition ? Expression.ParseCondition(text, context, "reaction:" + name, at) : Expression.Parse(text, context, "reaction:" + name, at); }
                catch (FormatException e) { throw new FormatException($"{at}: {e.Message}", e); }
            }
            if (!string.IsNullOrWhiteSpace(condition)) reaction.Condition = Parse(condition, Where("condition"), asCondition: true);
            if (signal == SignalKind.Every)
            {
                reaction.Period = rs.Period ?? 0.0;
                if (!(reaction.Period > 0) || double.IsInfinity(reaction.Period))
                    throw new FormatException(Where($"the period {reaction.Period} must be > 0."));
                double first = rs.FirstTime ?? double.NaN;
                if (!double.IsNaN(first) && first < 0) throw new FormatException(Where($"the first time {first} must be ≥ 0."));
                reaction.First = first;
            }
            if (signal == SignalKind.AtTime)
            {
                reaction.At = rs.AtTime ?? double.NaN;
                if (double.IsNaN(reaction.At) || reaction.At < 0) throw new FormatException(Where("give the time (at ≥ 0)."));
            }

            bool itemSignal = SignalKinds.HasItem(signal);
            int n = 0;
            foreach (var s in rs.Actions ?? new List<ReactionActionSettings>())
            {
                n++;
                if (s == null) continue;
                string at = Where($"action {n}");
                var a = new ReactionAction { Kind = s.Kind };
                string target = s.Target;
                a.Name = s.Name?.Trim();
                // the value: a plain number, else an expression's text
                string text = s.Value?.Trim();
                a.Value = string.IsNullOrEmpty(text) ? new ReactionValue(double.NaN)
                        : double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number)
                          ? new ReactionValue(number) : new ReactionValue(Parse(text, at));
                if (!string.IsNullOrWhiteSpace(s.OnlyIf)) a.Condition = Parse(s.OnlyIf, at + " (only if)", asCondition: true);
                a.Group = s.Group?.Trim() ?? "";
                switch (a.Kind)
                {
                    case ReactionActionKind.SetItemLabel:
                        if (!itemSignal) throw new FormatException($"{at}: an item label needs an item signal (ItemEntered / ItemExited / ProcessEnded / WaitTimedOut), not {signal}.");
                        Require(a, at, value: true);
                        if (a.Value.Expression != null && !string.IsNullOrEmpty(a.Name)) a.Value.Expression.WritesLabel = a.Name;   // the build check of computed names
                        break;
                    case ReactionActionKind.SetElementProperty:
                        a.Target = Resolve(target, elements, at);
                        Require(a, at, value: true);
                        break;
                    case ReactionActionKind.Stop:
                        a.Team = Team(s.Team, teams, at);
                        a.Count = Count(s.Count ?? 0, at);
                        a.Target = a.Team == null ? Resolve(target, elements, at) : null;
                        if (a.Team == null && a.Target == null && timeSignal)
                            throw new FormatException($"{at}: choose the element or team to stop (a time signal has no element of its own).");
                        if (!string.IsNullOrWhiteSpace(s.State)) a.State = ElementState.Of(s.State.Trim());
                        a.Mode = s.Mode;
                        a.Duration = s.Duration ?? 0.0;
                        if (a.Duration < 0 || double.IsNaN(a.Duration)) throw new FormatException($"{at}: duration {a.Duration} must be ≥ 0.");
                        break;
                    case ReactionActionKind.Resume:
                        a.Team = Team(s.Team, teams, at);
                        a.Count = Count(s.Count ?? 0, at);
                        a.Target = a.Team == null ? Resolve(target, elements, at) : null;
                        if (a.Team == null && a.Target == null && timeSignal)
                            throw new FormatException($"{at}: choose the element or team to resume (a time signal has no element of its own).");
                        break;
                    case ReactionActionKind.Release:
                        a.Target = Resolve(target, elements, at);
                        if (a.Target == null && timeSignal)
                            throw new FormatException($"{at}: choose the Queue to release (a time signal has no element of its own).");
                        if (a.Target != null && !(a.Target is ItemsQueue q && q.Gated))
                            throw new FormatException($"{at}: a release acts on a Queue released on command, not on '{a.Target.Name}'.");
                        if (a.Value.IsSet && a.Value.Expression == null && (a.Value.Number < 0 || a.Value.Number != Math.Floor(a.Value.Number)))
                            throw new FormatException($"{at}: the number of items to release ({a.Value.Number}) must be a whole number ≥ 0.");
                        break;
                    case ReactionActionKind.Retry:
                        a.Target = Resolve(target, elements, at);
                        if (a.Target == null && timeSignal)
                            throw new FormatException($"{at}: choose the element to offer again (a time signal has no element of its own).");
                        break;
                    case ReactionActionKind.SetItemLook:
                    {
                        if (!itemSignal) throw new FormatException($"{at}: an item look needs an item signal (ItemEntered / ItemExited / ProcessEnded / WaitTimedOut), not {signal}.");
                        LookColor? color = null;
                        if (!string.IsNullOrWhiteSpace(s.Color))
                        {
                            if (!LookColor.TryParse(s.Color, out var c, out string error)) throw new FormatException($"{at}: {error}.");
                            color = c;
                        }
                        double scale = s.Scale ?? double.NaN;
                        if (!double.IsNaN(scale) && (!(scale > 0) || double.IsInfinity(scale)))
                            throw new FormatException($"{at}: the scale {scale} must be > 0.");
                        if (color == null && double.IsNaN(scale)) throw new FormatException($"{at}: give the colour or the scale of the item.");
                        a.Look = new ItemLook(color, scale);
                        break;
                    }
                    case ReactionActionKind.PushToList:
                        if (string.IsNullOrEmpty(a.Name)) throw new FormatException($"{at}: choose the list.");
                        if (clock.Context.GetList(a.Name) == null)
                            throw new FormatException($"{at}: no list '{a.Name}' in the model.");
                        if (!a.Value.IsSet) throw new FormatException($"{at}: give the value to push.");
                        break;
                }
                reaction.Actions.Add(a);
            }

            if (timeSignal)
            {
                if (cfg.Sources.Count > 0)
                    throw new FormatException(Where($"a time signal ({signal}) listens to no element; read elements by name in the expressions (content('X'))."));
                return reaction;
            }
            foreach (string id in cfg.Sources)
                reaction.Listen(Resolve(id, elements, Where("source")) ?? throw new FormatException(Where("a source is empty.")));
            if (reaction.Sources.Count == 0) throw new FormatException(Where("no element to listen to."));
            return reaction;
        }

        /// <summary>A team by id, else by name; null for no reference.</summary>
        private static SimuLean.Resources.ResourceDispatcher Team(string reference, IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> teams, string at)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            if (teams != null)
            {
                if (teams.TryGetValue(reference, out var byId)) return byId;
                foreach (var t in teams.Values) if (t.Name == reference) return t;
            }
            throw new FormatException($"{at}: no resource team '{reference}' in the model.");
        }

        private static int Count(int value, string at)
        {
            if (value < 0) throw new FormatException($"{at}: the number of members {value} must be ≥ 0 (0 = all).");
            return value;
        }

        private static void Require(ReactionAction a, string at, bool value)
        {
            if (string.IsNullOrEmpty(a.Name)) throw new FormatException($"{at}: give the name to set.");
            if (value && !a.Value.IsSet) throw new FormatException($"{at}: give the value of '{a.Name}'.");
        }

        /// <summary>An element by id, else by display name; null for an empty reference (= the signal's element).</summary>
        private static Element Resolve(string reference, IReadOnlyDictionary<string, Element> elements, string at)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            if (elements.TryGetValue(reference, out var byId)) return byId;
            Element found = null;
            foreach (var e in elements.Values)
                if (e.Name == reference)
                {
                    if (found != null) throw new FormatException($"{at}: several elements are named '{reference}'.");
                    found = e;
                }
            return found ?? throw new FormatException($"{at}: no element '{reference}' in the model.");
        }
    }
}
