using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Serialization;
using SimuLean.Signals;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// "When [elements] [signal] if [condition] do [actions]": a scene component, not an element. The scene
    /// extraction exports it as a <see cref="ReactionConfig"/>; the engine builds the <see cref="Reaction"/> (scene and
    /// headless alike) and the clock binds it here (<see cref="Bind"/>) so the views can show what it did. The element
    /// inspector lists the reactions that listen to an element and creates new ones (+ Reaction).
    /// </summary>
    [AddComponentMenu("SimuLean/Reaction")]
    [Icon("Assets/SimuLean.Unity/Editor/Icons/Reaction.png")]
    [SimuLeanTool(SimuLeanToolCategories.Logic, "Reaction", Icon = "Assets/SimuLean.Unity/Editor/Icons/Reaction.png",
        Description = "When an element signals (an item enters or leaves, a process ends, a state changes, a stop starts or ends) or the clock does (run start, every period, a time) and a condition holds, do actions: set a label or a property, stop / resume an element or team members, push to a list")]
    public class UnityReaction : MonoBehaviour
    {
        [Serializable]
        public class ActionSpec
        {
            [Tooltip("Set Item Label: a label of the signal's item. Set Element Property: a property of an element (rules read it " +
                     "with get('Element', 'name')). Stop / Resume: an element. Push To List: a value to a list.")]
            public ReactionActionKind kind = ReactionActionKind.SetElementProperty;
            [Tooltip("Stop / Resume: members of a resource team instead of an element (e.g. call an extra operator).")]
            public bool onTeam;
            [Tooltip("The element it acts on (empty = the element that signalled).")]
            public SElement target;
            [Tooltip("Stop / Resume: the team.")]
            public UnityResourceTeam team;
            [Tooltip("Stop / Resume: how many members (0 = all). Stop takes the last members still working; Resume calls back " +
                     "those reactions stopped, in the order they were stopped.")]
            [Min(0)] public int count = 1;
            [Tooltip("The label / property name.")]
            public string name = "";
            [Tooltip("The list (Push To List).")]
            public UnitySimList list;
            [Tooltip("A number (a study can vary it) or an expression over the item and the element: item.weight * 2, " +
                     "element.content, now(), 'red'.")]
            public string value = "";
            [Tooltip("Stop: the state shown while stopped.")]
            public string state = "STOPPED";
            [Tooltip("Stop: Immediate pauses the work in progress; After Current lets the current job finish.")]
            public StopMode mode = StopMode.Immediate;
            [Tooltip("Stop: how long (0 = until a Resume action of any reaction in the same group).")]
            [Min(0)] public double duration = 0;
            [Tooltip("Stop / Resume: the group of the stop (empty = the default). A Resume ends only the stops of its group, so " +
                     "independent controls on one element (orders gating a station, an incident) do not end each other's stops.")]
            public string group = "";
            [Tooltip("Only if: the action runs only when this holds (empty = always), checked after the actions before it, " +
                     "e.g. decrement a counter, then stop if get('DC', 'open') <= 0.")]
            public string onlyIf = "";
        }

        [Tooltip("Off: the reaction does nothing (a study can switch it: reaction:Name.enabled).")]
        public bool reactionEnabled = true;
        [Tooltip("The elements it listens to.")]
        public List<SElement> sources = new List<SElement>();
        [Tooltip("What it listens for.")]
        public SignalKind signal = SignalKind.ItemEntered;
        [Tooltip("State Changed: only changes to this state; Stopped / Resumed: only stops in this state (empty = any).")]
        public string state = "";
        [Tooltip("Every: the period (model time units).")]
        [Min(0)] public double period = 3600;
        [Tooltip("Every: the first time (negative = one period after the start).")]
        public double firstTime = -1;
        [Tooltip("At Time: the time (model time units).")]
        [Min(0)] public double atTime = 0;
        [Tooltip("Optional expression over the item and the element (empty = always): element.content >= 5, item.type == 'A'.")]
        public string condition = "";
        [Tooltip("Next Event (default): after the step that signalled. Immediate: inside it, before anything else moves.")]
        public SignalDelivery timing = SignalDelivery.NextEvent;
        public List<ActionSpec> actions = new List<ActionSpec>();

        /// <summary>The reaction of the current run (null outside Play).</summary>
        [NonSerialized] public Reaction Bound;

        /// <summary>Its name in the model: the GameObject's.</summary>
        public string ConfigId => gameObject.name;

        /// <summary>Binds the reaction the model built from this component for this run.</summary>
        public void Bind(Reaction built) => Bound = built;

        /// <summary>True when one of its actions stops or resumes members of <paramref name="t"/>.</summary>
        public bool Involves(UnityResourceTeam t)
        {
            if (t == null) return false;
            foreach (var a in actions) if (a != null && a.onTeam && a.team == t) return true;
            return false;
        }

        /// <summary>A time signal: no elements of its own.</summary>
        public bool IsTimeSignal => SignalKinds.IsTime(signal);

        /// <summary>True when it listens to <paramref name="element"/> or acts on it.</summary>
        public bool Involves(SElement element)
        {
            if (element == null) return false;
            if (!IsTimeSignal && sources.Contains(element)) return true;   // a clock signal's leftover elements do not count
            foreach (var a in actions) if (a != null && !a.onTeam && a.target == element) return true;
            return false;
        }

        /// <summary>
        /// This component as a <see cref="ReactionConfig"/>, naming elements with <paramref name="idOf"/> (null = not in the
        /// model: a source is left out; an action's target fails the build, naming it).
        /// </summary>
        public ReactionConfig ToConfig(Func<SElement, string> idOf, Func<UnityResourceTeam, string> teamIdOf = null)
        {
            var cfg = new ReactionConfig { Id = ConfigId };
            if (!IsTimeSignal)
                foreach (var s in sources)
                {
                    string id = s != null ? idOf(s) : null;
                    if (id != null) cfg.Sources.Add(id);
                }
            if (signal == SignalKind.Every)
            {
                cfg.Parameters["period"] = period;
                if (firstTime >= 0) cfg.Parameters["first"] = firstTime;
            }
            if (signal == SignalKind.AtTime) cfg.Parameters["at"] = atTime;
            cfg.Parameters["enabled"] = reactionEnabled;
            cfg.Parameters["signal"] = signal.ToString();
            cfg.Parameters["delivery"] = timing.ToString();
            if (!string.IsNullOrWhiteSpace(state)) cfg.Parameters["state"] = state.Trim();
            if (!string.IsNullOrWhiteSpace(condition)) cfg.Parameters["condition"] = condition.Trim();
            foreach (var a in actions)
            {
                if (a == null) continue;
                bool stopResume = a.kind == ReactionActionKind.Stop || a.kind == ReactionActionKind.Resume;
                bool team = stopResume && a.onTeam;
                string target = !team && a.target != null ? idOf(a.target) ?? a.target.name : null;
                string teamId = team && a.team != null ? (teamIdOf != null ? teamIdOf(a.team) : null) ?? a.team.TeamName : null;
                string name = a.kind == ReactionActionKind.PushToList ? (a.list != null ? a.list.ListName : null) : a.name?.Trim();
                bool stop = a.kind == ReactionActionKind.Stop;
                cfg.AddAction(a.kind.ToString(), target: target, name: name, value: Value(a.value),
                              state: stop && !string.IsNullOrWhiteSpace(a.state) ? a.state.Trim() : null,
                              mode: stop ? a.mode.ToString() : null, duration: stop ? a.duration : 0,
                              team: teamId, count: team ? a.count : 0,
                              group: stopResume ? a.group : null, condition: a.onlyIf);
            }
            return cfg;
        }

        /// <summary>A plain number as a number (a catalog parameter), anything else as an expression; empty = none.</summary>
        public static object Value(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return n;
            return text;
        }
    }
}
