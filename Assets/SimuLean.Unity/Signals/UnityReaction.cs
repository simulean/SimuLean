using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Serialization;
using SimuLean.Signals;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// "When [elements] [signal] if [condition] do [actions]" a scene component, not an element. The scene
    /// extraction exports it as a <see cref="ReactionConfig"/>; the engine builds the <see cref="Reaction"/> (scene and
    /// headless alike) and the clock binds it here (<see cref="Bind"/>) so the views can show what it did. The element
    /// inspector lists the reactions that listen to an element and creates new ones (+ Reaction).
    /// </summary>
    [AddComponentMenu("SimuLean/Reaction")]
    [Icon("Assets/SimuLean.Unity/Editor/Icons/Reaction.png")]
    [SimuLeanTool(SimuLeanToolCategories.Logic, "Reaction", Icon = "Assets/SimuLean.Unity/Editor/Icons/Reaction.png",
        Description = "When an element signals (an item enters or leaves, a process ends, a state changes, a stop starts or ends) or the clock does (run start, every period, a time) and a condition holds, do actions: set a label or a property, stop / resume an element or team members, push to a list, change an item's look")]
    public class UnityReaction : MonoBehaviour
    {
        [Serializable]
        public class ActionSpec
        {
            [Tooltip("Set Item Label: a label of the signal's item. Set Element Property: a property of an element (rules read it " +
                     "with get('Element', 'name')). Stop / Resume: an element. Push To List: a value to a list. Set Item Look: the " +
                     "colour / scale of the item's visual (view only).")]
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
            [SimuLean.Unity.SimuLeanExpression("reaction")]
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
            [SimuLean.Unity.SimuLeanExpression("reaction")]
            public string onlyIf = "";
            [Tooltip("Set Item Look: the colour of the item's visual (its alpha too; fully transparent = keep it). View only: no " +
                     "label, no result changes.")]
            [SimuLeanHelp("A colour as '#RRGGBB' (or '#RRGGBBAA', or a name: red, green, blue, yellow…), e.g. actions[0].color = #E53935. " +
                          "A reaction whose every action is Set Item Look is view only (inferred: the logic report marks it viewOnly; no result changes).")]
            public Color color = KeepColor;
            [Tooltip("Set Item Look: the scale of the item's visual, a factor of its own size (0 = keep it). View only: the " +
                     "item's size labels, which lanes and packing read, do not change.")]
            [Min(0)] public float scale = 0;

            /// <summary>Set Item Look: "keep the item's colour" (a fully transparent colour, the default).</summary>
            public static readonly Color KeepColor = new Color(0, 0, 0, 0);

            /// <summary>Set Item Look: true when it changes the item's colour (a colour that is not fully transparent).</summary>
            public bool SetsColor => color.a > 0;
        }

        [Tooltip("Off: the reaction does nothing (a study can switch it: reaction:Name.reactionEnabled).")]
        public bool reactionEnabled = true;
        [Tooltip("The elements it listens to.")]
        public List<SElement> sources = new List<SElement>();
        [Tooltip("What it listens for.")]
        public SignalKind signal = SignalKind.ItemEntered;
        [Tooltip("State Changed: only changes to this state; Stopped / Resumed: only stops in this state (empty = any).")]
        public string state = "";
        [Tooltip("Every: the period (model time units).")]
        [Min(0)] public double period = 3600;
        [Tooltip("Every: the first time (negative — the default -1 — = one period after the start; 0 = at the start too).")]
        [SimuLeanHelp("Every fires at firstTime (-1, the default: one period after the start), then each period, up to and " +
                      "including the horizon — every event at the horizon time runs, as arrivals and process ends do: period 1800 " +
                      "with horizon 28800 fires 16 times (1800 … 28800), 17 with firstTime = 0 (0 … 28800).")]
        public double firstTime = -1;
        [Tooltip("At Time: the time (model time units).")]
        [Min(0)] public double atTime = 0;
        [Tooltip("Optional expression over the item and the element (empty = always): element.content >= 5, item.type == 'A'.")]
        [SimuLean.Unity.SimuLeanExpression("reaction")]
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
            var rs = cfg.Settings;
            if (signal == SignalKind.Every)
            {
                rs.Period = period;
                if (firstTime >= 0) rs.FirstTime = firstTime;
            }
            if (signal == SignalKind.AtTime) rs.AtTime = atTime;
            rs.ReactionEnabled = reactionEnabled;
            rs.Signal = signal;
            rs.Timing = timing;
            if (!string.IsNullOrWhiteSpace(state)) rs.State = state.Trim();
            if (!string.IsNullOrWhiteSpace(condition)) rs.Condition = condition.Trim();
            foreach (var a in actions)
            {
                if (a == null) continue;
                bool stopResume = a.kind == ReactionActionKind.Stop || a.kind == ReactionActionKind.Resume;
                bool team = stopResume && a.onTeam;
                string target = !team && a.target != null ? idOf(a.target) ?? a.target.name : null;
                string teamId = team && a.team != null ? (teamIdOf != null ? teamIdOf(a.team) : null) ?? a.team.TeamName : null;
                bool look = a.kind == ReactionActionKind.SetItemLook;
                string name = a.kind == ReactionActionKind.PushToList ? (a.list != null ? a.list.ListName : null) : look ? null : a.name?.Trim();
                bool stop = a.kind == ReactionActionKind.Stop;
                cfg.AddAction(a.kind.ToString(), target: look ? null : target, name: name,
                              value: look || string.IsNullOrWhiteSpace(a.value) ? null : a.value.Trim(),
                              state: stop && !string.IsNullOrWhiteSpace(a.state) ? a.state.Trim() : null,
                              mode: stop ? a.mode.ToString() : null, duration: stop ? a.duration : 0,
                              team: teamId, count: team ? a.count : 0,
                              group: stopResume ? a.group : null, condition: a.onlyIf,
                              color: look && a.SetsColor ? ColorText(a.color) : null, scale: look && a.scale > 0 ? a.scale : 0);
            }
            return cfg;
        }

        /// <summary>A colour as the engine reads it: <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when not opaque.</summary>
        public static string ColorText(Color c)
            => "#" + (c.a < 1f ? ColorUtility.ToHtmlStringRGBA(c) : ColorUtility.ToHtmlStringRGB(c));

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
