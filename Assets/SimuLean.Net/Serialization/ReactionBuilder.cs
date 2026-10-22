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

            var signal = ParseEnum<SignalKind>(cfg.GetParameter("signal", nameof(SignalKind.ItemEntered)), Where("signal"));
            var reaction = new Reaction(name, signal, clock)
            {
                Enabled = cfg.GetParameter("enabled", true),
                Delivery = ParseEnum<SignalDelivery>(cfg.GetParameter("delivery", nameof(SignalDelivery.NextEvent)), Where("delivery")),
            };
            string state = cfg.GetParameter<string>("state", null);
            if (!string.IsNullOrWhiteSpace(state)) reaction.State = ElementState.Of(state.Trim());
            string condition = cfg.GetParameter<string>("condition", null);
            if (!string.IsNullOrWhiteSpace(condition)) reaction.Condition = Parse(condition, Where("condition"));

            bool timeSignal = SignalKinds.IsTime(signal);
            if (signal == SignalKind.Every)
            {
                reaction.Period = cfg.GetParameter("period", 0.0);
                if (!(reaction.Period > 0) || double.IsInfinity(reaction.Period))
                    throw new FormatException(Where($"the period {reaction.Period} must be > 0."));
                double first = cfg.GetParameter("first", double.NaN);
                if (!double.IsNaN(first) && first < 0) throw new FormatException(Where($"the first time {first} must be ≥ 0."));
                reaction.First = first;
            }
            if (signal == SignalKind.AtTime)
            {
                reaction.At = cfg.GetParameter("at", double.NaN);
                if (double.IsNaN(reaction.At) || reaction.At < 0) throw new FormatException(Where("give the time (at ≥ 0)."));
            }

            bool itemSignal = SignalKinds.HasItem(signal);
            for (int n = 1; cfg.Parameters.ContainsKey(ReactionConfig.ActionKey(n, "kind")); n++)
            {
                string Key(string field) => ReactionConfig.ActionKey(n, field);
                string at = Where($"action {n}");
                var a = new ReactionAction { Kind = ParseEnum<ReactionActionKind>(cfg.GetParameter<string>(Key("kind")), at) };
                string target = cfg.GetParameter<string>(Key("target"), null);
                a.Name = cfg.GetParameter<string>(Key("name"), null)?.Trim();
                string expression = cfg.GetParameter<string>(Key("expression"), null);
                a.Value = !string.IsNullOrWhiteSpace(expression) ? new ReactionValue(Parse(expression, at))
                        : new ReactionValue(cfg.GetParameter(Key("number"), double.NaN));
                string onlyIf = cfg.GetParameter<string>(Key("condition"), null);
                if (!string.IsNullOrWhiteSpace(onlyIf)) a.Condition = Parse(onlyIf, at + " (only if)");
                a.Group = cfg.GetParameter<string>(Key("group"), null)?.Trim() ?? "";
                switch (a.Kind)
                {
                    case ReactionActionKind.SetItemLabel:
                        if (!itemSignal) throw new FormatException($"{at}: an item label needs an item signal (ItemEntered / ItemExited / ProcessEnded), not {signal}.");
                        Require(a, at, value: true);
                        break;
                    case ReactionActionKind.SetElementProperty:
                        a.Target = Resolve(target, elements, at);
                        Require(a, at, value: true);
                        break;
                    case ReactionActionKind.Stop:
                        a.Team = Team(cfg.GetParameter<string>(Key("team"), null), teams, at);
                        a.Count = Count(cfg.GetParameter(Key("count"), 0.0), at);
                        a.Target = a.Team == null ? Resolve(target, elements, at) : null;
                        if (a.Team == null && a.Target == null && timeSignal)
                            throw new FormatException($"{at}: choose the element or team to stop (a time signal has no element of its own).");
                        string st = cfg.GetParameter<string>(Key("state"), null);
                        if (!string.IsNullOrWhiteSpace(st)) a.State = ElementState.Of(st.Trim());
                        a.Mode = ParseEnum<StopMode>(cfg.GetParameter(Key("mode"), nameof(StopMode.Immediate)), at);
                        a.Duration = cfg.GetParameter(Key("duration"), 0.0);
                        if (a.Duration < 0 || double.IsNaN(a.Duration)) throw new FormatException($"{at}: duration {a.Duration} must be ≥ 0.");
                        break;
                    case ReactionActionKind.Resume:
                        a.Team = Team(cfg.GetParameter<string>(Key("team"), null), teams, at);
                        a.Count = Count(cfg.GetParameter(Key("count"), 0.0), at);
                        a.Target = a.Team == null ? Resolve(target, elements, at) : null;
                        if (a.Team == null && a.Target == null && timeSignal)
                            throw new FormatException($"{at}: choose the element or team to resume (a time signal has no element of its own).");
                        break;
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

        private static int Count(double value, string at)
        {
            if (value < 0 || value != Math.Floor(value)) throw new FormatException($"{at}: the number of members {value} must be a whole number ≥ 0 (0 = all).");
            return (int)value;
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
                if (e.GetName() == reference)
                {
                    if (found != null) throw new FormatException($"{at}: several elements are named '{reference}'.");
                    found = e;
                }
            return found ?? throw new FormatException($"{at}: no element '{reference}' in the model.");
        }

        private static Expression Parse(string text, string at)
        {
            string error = Expression.Check(text);
            if (error != null) throw new FormatException($"{at}: {error}");
            return Expression.Parse(text);
        }

        private static T ParseEnum<T>(string text, string at) where T : struct, Enum
        {
            if (Enum.TryParse(text?.Trim(), true, out T value) && Enum.IsDefined(typeof(T), value)) return value;
            throw new FormatException($"{at}: '{text}' is not one of {string.Join(", ", Enum.GetNames(typeof(T)))}.");
        }
    }
}
