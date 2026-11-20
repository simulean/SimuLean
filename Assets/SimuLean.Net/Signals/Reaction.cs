using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Expressions;

namespace SimuLean.Signals
{
    /// <summary>What a reaction does (first version).</summary>
    public enum ReactionActionKind
    {
        /// <summary>Sets a label of the signal's item (<c>item.name = value</c>).</summary>
        SetItemLabel,
        /// <summary>Sets a property of an element (routing / admission rules read it with <c>get('Element', 'name')</c>).</summary>
        SetElementProperty,
        /// <summary>Stops an element, or members of a resource team (a state, a mode), for a time or until a
        /// <see cref="Resume"/> action.</summary>
        Stop,
        /// <summary>Ends the stops reactions started on an element, or on members of a team, in its group (any reaction: one
        /// stops a line, another resumes it; one sends an operator off, another calls them back).</summary>
        Resume,
        /// <summary>Pushes a value to a model list.</summary>
        PushToList,
        /// <summary>Releases items held by a gated Queue (the element, or the one that signalled): the value is how many
        /// (empty = 1).</summary>
        Release,
        /// <summary>Offers an element (empty = the one that signalled) again the items waiting for it upstream, so its admission
        /// is evaluated again for a condition that reads something other than an element property
        /// (<c>content('Buffer') &lt; 5</c>, a table) — a property write wakes it by itself.</summary>
        Retry,
        /// <summary>Changes how the signal's item looks in the 3D view: its colour and / or scale. View-only:
        /// nothing scheduled, no label or property written, every result unchanged (headless runs ignore it).</summary>
        SetItemLook,
    }

    /// <summary>A value of an action: a plain number (a study can vary it) or an expression over the item and the
    /// element.</summary>
    public readonly struct ReactionValue
    {
        public readonly double Number;
        public readonly Expression Expression;
        public ReactionValue(double number) { Number = number; Expression = null; }
        public ReactionValue(Expression expression) { Number = 0; Expression = expression; }
        public bool IsSet => Expression != null || !double.IsNaN(Number);
        public ExprValue Evaluate(IExpressionScope scope) => Expression != null ? Expression.Evaluate(scope) : ExprValue.Of(Number);
        public override string ToString() => Expression?.Text ?? Number.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>One action of a reaction.</summary>
    public sealed class ReactionAction
    {
        public ReactionActionKind Kind;
        /// <summary>The element it acts on (null = the signal's element); unused by item labels and lists.</summary>
        public Element Target;
        /// <summary>The label / property name; the list's name for <see cref="ReactionActionKind.PushToList"/>.</summary>
        public string Name;
        public ReactionValue Value;
        /// <summary>Stops: the state shown, the mode, and how long (0 = until a resume action).</summary>
        public ElementState State = ElementState.STOPPED;
        public StopMode Mode = StopMode.Immediate;
        public double Duration;
        /// <summary>Stop / resume members of this team instead of an element.</summary>
        public SimuLean.Resources.ResourceDispatcher Team;
        /// <summary>Team: how many members (0 = all). Stop takes the last members still working; resume calls back those
        /// reactions stopped, in the order they were stopped.</summary>
        public int Count;
        /// <summary>Stop / Resume: the group of the stop (empty = the default group). A resume ends only the stops of its
        /// group, so independent controls on one element (orders gating a station, an incident stopping it) do not end
        /// each other's stops; the element runs again when none is left.</summary>
        public string Group = "";
        /// <summary>Only if: the action runs only when this holds (null = always), evaluated when its turn comes, after the
        /// actions before it (e.g. decrement a counter, then stop if it reached zero).</summary>
        public Expression Condition;
        /// <summary>Set Item Look: the colour / scale it gives the item.</summary>
        public ItemLook Look;
    }

    /// <summary>
    /// "When [elements] [signal] if [condition] do [actions]": subscribes to a signal of its source elements in
    /// the model's <see cref="SignalHub"/> and, each time the signal passes its state filter and condition, runs its actions
    /// in order (each with its optional "only if"). Several reactions on one signal run in the order they were built. Expressions see the signal's item and element (<c>item.x</c>, <c>element.content</c>, <c>get('E', 'p')</c>,
    /// data tables); random functions draw from the reaction's own stream. Counts what it did (the Play inspector shows it).
    /// A failing action stops the run with an error naming the reaction.
    /// </summary>
    public sealed class Reaction : ISignalSink, IWarmupStatistics
    {
        public string Name { get; }
        public SignalKind Signal { get; }
        public bool Enabled { get; set; } = true;
        /// <summary>State changes: only to this state; stops / resumes: only stops in this state (null = any).</summary>
        public ElementState? State { get; set; }
        /// <summary>Null = always.</summary>
        public Expression Condition { get; set; }
        /// <summary>Time signals: the period (<see cref="SignalKind.Every"/>), its first time (NaN = one period), the time
        /// (<see cref="SignalKind.AtTime"/>).</summary>
        public double Period { get; set; }
        public double First { get; set; } = double.NaN;
        public double At { get; set; }
        public SignalDelivery Delivery { get; set; } = SignalDelivery.NextEvent;
        public List<ReactionAction> Actions { get; } = new List<ReactionAction>();
        public IReadOnlyList<Element> Sources => sources;

        /// <summary>True when it listens to <paramref name="element"/> or one of its actions acts on it.</summary>
        public bool Involves(Element element)
        {
            if (element == null) return false;
            if (sources.Contains(element)) return true;
            foreach (var a in Actions) if (a.Target == element) return true;
            return false;
        }

        /// <summary>Times its actions ran.</summary>
        public int Fired { get; private set; }
        /// <summary>Signals its condition turned down.</summary>
        public int Skipped { get; private set; }
        public double LastFired { get; private set; } = double.NaN;
        /// <summary>The item of the last firing ("Type #id"), or null.</summary>
        public string LastItem { get; private set; }
        public Element LastSource { get; private set; }

        private readonly List<Element> sources = new List<Element>();
        private readonly SimClock clock;
        private readonly Scope scope;

        public Reaction(string name, SignalKind signal, SimClock clock)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Reaction" : name;
            Signal = signal;
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            scope = new Scope(this);
        }

        /// <summary>Listens to <paramref name="source"/> (the model's signal table).</summary>
        public void Listen(Element source)
        {
            if (source == null || sources.Contains(source)) return;
            sources.Add(source);
            clock.Context.Signals.Register(this);
            clock.Context.Signals.Subscribe(source, Signal, this, Delivery);
        }

        /// <summary>A new run: counts and the reactions' stops forgotten.</summary>
        public void Reset()
        {
            Fired = Skipped = 0; LastFired = double.NaN; LastItem = null; LastSource = null;
            clock.Context.Signals.ReactionStops.Clear();
            clock.Context.Signals.StoppedMembers.Clear();
            clock.Context.Signals.Register(this);
        }

        /// <summary>The warm-up: the counts start again (the reaction carries on).</summary>
        public void ResetStatistics() { Fired = Skipped = 0; }

        /// <summary>Time signals: schedules the first one (the model's <c>StartModel</c>, after the elements started).</summary>
        public void Start()
        {
            switch (Signal)
            {
                case SignalKind.RunStarted: clock.ScheduleEvent(new Tick(this, 0), 0); break;
                case SignalKind.AtTime: clock.ScheduleEvent(new Tick(this, 0), Math.Max(0, At - clock.GetSimulationTime())); break;
                case SignalKind.Every:
                    double first = double.IsNaN(First) ? Period : First;
                    clock.ScheduleEvent(new Tick(this, Period), Math.Max(0, first - clock.GetSimulationTime()));
                    break;
            }
        }

        /// <summary>A time signal: fires, then (every) comes again one period later.</summary>
        private sealed class Tick : IEvent
        {
            private readonly Reaction owner; private readonly double period;
            public Tick(Reaction owner, double period) { this.owner = owner; this.period = period; }
            public void Execute()
            {
                if (period > 0) owner.clock.ScheduleEvent(this, period);
                ((ISignalSink)owner).OnSignal(new SignalArgs(owner.Signal, null, null, default, default, owner.clock.GetSimulationTime()));
            }
        }

        private Dictionary<Element, List<(StopToken token, string group)>> Stops => clock.Context.Signals.ReactionStops;

        void ISignalSink.OnSignal(in SignalArgs s)
        {
            if (!Enabled) return;
            if (State.HasValue && SignalKinds.HasState(Signal) && !s.To.Equals(State.Value)) return;
            scope.Item = s.Item; scope.Element = s.Source; scope.Signal = s;
            try
            {
                if (Condition != null && !Condition.Condition(scope)) { Skipped++; return; }
                Fired++;
                LastFired = s.Time; LastSource = s.Source;
                LastItem = s.Item != null ? $"{s.Item.Name} #{s.Item.GetId()}" : null;
                foreach (var a in Actions)
                    if (a.Condition == null || a.Condition.Condition(scope)) Run(a, s);
            }
            catch (Exception e) when (!(e is OutOfMemoryException) && !(e is ReactionException))
            {
                string where = s.Source != null ? " of '" + s.Source.Name + "'" : "";
                throw new ReactionException($"Reaction '{Name}' (on {s.Kind}{where} at t = {s.Time.ToString("0.###", CultureInfo.InvariantCulture)}): {e.Message}", e);
            }
            finally { scope.Item = null; scope.Element = null; scope.Signal = default; }
        }

        private void Run(ReactionAction a, in SignalArgs s)
        {
            Element target = a.Target ?? s.Source;
            switch (a.Kind)
            {
                case ReactionActionKind.SetItemLabel:
                {
                    if (s.Item == null) return;
                    s.Item.Properties.Set(a.Name, a.Value.Evaluate(scope));
                    return;
                }
                case ReactionActionKind.SetElementProperty:
                {
                    target.Properties.Set(a.Name, a.Value.Evaluate(scope));
                    return;
                }
                case ReactionActionKind.Stop:
                    if (a.Team != null) { StopMembers(a); return; }
                    if (target == null) throw new InvalidOperationException("a stop needs an element (a time signal has none).");
                    StopOne(target, a);
                    return;
                case ReactionActionKind.Resume:
                {
                    if (a.Team != null) { ResumeMembers(a); return; }
                    if (target != null) EndGroup(target, Group(a));
                    return;
                }
                case ReactionActionKind.PushToList:
                {
                    var list = clock.Context.GetList(a.Name)
                               ?? throw new InvalidOperationException($"no list '{a.Name}' in the model.");
                    var v = a.Value.Evaluate(scope);
                    list.PushValue(v.IsText ? (object)v.Text : v.AsNumber(), null, s.Source);
                    return;
                }
                case ReactionActionKind.Release:
                {
                    if (!(target is ItemsQueue gate) || !gate.Gated)
                        throw new InvalidOperationException($"a release acts on a Queue released on command, not on '{target?.Name ?? "(none)"}'.");
                    double n = a.Value.IsSet ? a.Value.Evaluate(scope).AsNumber() : 1;
                    if (double.IsNaN(n) || n < 0 || n != Math.Floor(n))
                        throw new InvalidOperationException($"the number of items to release ({n}) must be a whole number ≥ 0.");
                    if (n > 0) gate.ReleaseItems((int)n);
                    return;
                }
                case ReactionActionKind.Retry:
                    if (target == null) throw new InvalidOperationException("a retry needs an element (a time signal has none).");
                    target.WakeInput();
                    return;
                case ReactionActionKind.SetItemLook:
                    s.Item?.SetLook(a.Look);   // view-only: nothing scheduled, nothing read by the model
                    return;
            }
        }

        private void StopOne(Element target, ReactionAction a)
        {
            var token = target.Stop(new StopRequest { State = a.State, Mode = a.Mode, Reason = "Reaction " + Name });
            if (!Stops.TryGetValue(target, out var list)) Stops[target] = list = new List<(StopToken, string)>(1);
            list.Add((token, Group(a)));
            // its duration from when it takes effect: an AfterCurrent stop starts when the work in hand ends, whole
            if (a.Duration > 0) token.WhenEffective(t => clock.ScheduleEvent(new ResumeAfter(this, target, t), a.Duration));
            if (a.Team != null) StoppedMembers.Add((target, Group(a)));
        }

        private static string Group(ReactionAction a) => a.Group ?? "";

        // ends the reaction stops of one group on an element (the others stay), oldest first
        private void EndGroup(Element target, string group)
        {
            if (!Stops.TryGetValue(target, out var list)) return;
            var ended = new List<StopToken>();
            for (int i = 0; i < list.Count;)
                if (list[i].group == group) { ended.Add(list[i].token); list.RemoveAt(i); }
                else i++;
            foreach (var t in ended) target.Resume(t);
        }

        // the members reactions stopped (and the group), in the order they were stopped (a resume calls them back in that order)
        private List<(Element member, string group)> StoppedMembers => clock.Context.Signals.StoppedMembers;

        private void StopMembers(ReactionAction a)
        {
            int n = a.Count > 0 ? a.Count : int.MaxValue;
            var members = a.Team.Members;
            for (int i = members.Count - 1; i >= 0 && n > 0; i--)   // the last members still working first
            {
                var m = members[i];
                if (StoppedMembers.Exists(x => x.member == m)) continue;   // still working: stopped by no group
                StopOne(m, a);
                n--;
            }
        }

        private void ResumeMembers(ReactionAction a)
        {
            int n = a.Count > 0 ? a.Count : int.MaxValue;
            for (int i = 0; i < StoppedMembers.Count && n > 0;)
            {
                var (m, group) = StoppedMembers[i];
                bool ofTeam = false;
                foreach (var x in a.Team.Members) if (x == m) { ofTeam = true; break; }
                if (!ofTeam || group != Group(a)) { i++; continue; }
                StoppedMembers.RemoveAt(i);
                EndGroup(m, group);
                n--;
            }
        }

        private void EndStop(Element target, StopToken token)
        {
            if (Stops.TryGetValue(target, out var list))
            {
                int i = list.FindIndex(x => x.token == token);
                if (i >= 0)
                {
                    string group = list[i].group;
                    list.RemoveAt(i);
                    if (!list.Exists(x => x.group == group)) StoppedMembers.Remove((target, group));
                }
            }
            target.Resume(token);
        }

        private sealed class ResumeAfter : IEvent
        {
            private readonly Reaction owner; private readonly Element target; private readonly StopToken token;
            public ResumeAfter(Reaction owner, Element target, StopToken token) { this.owner = owner; this.target = target; this.token = token; }
            public void Execute() => owner.EndStop(target, token);
        }

        /// <summary>The expressions' scope: the signal's item and element, the model's data and elements, and the reaction's
        /// own random stream (so a reaction does not shift the draws of the element it listens to).</summary>
        private sealed class Scope : IExpressionScope, IDataScope, IRandomScope, IModelScope
        {
            private readonly Reaction owner;
            private SimuLean.Stochastics.Streams.IRandomEngine random;
            public Item Item;
            public Element Element;
            public SignalArgs Signal;
            public Scope(Reaction owner) { this.owner = owner; }
            public double Now => owner.clock.GetSimulationTime();
            public SimuLean.Data.ModelData Data => owner.clock.Context.Data;
            public SimuLean.Stochastics.Streams.IRandomEngine Random
                => random ?? (random = owner.clock.Context.CreateRandomStream("reaction:" + owner.Name, "expr"));
            public SimContext Model => owner.clock.Context;

            public ExprValue Get(string root, string name)
            {
                switch (root)
                {
                    case null:
                    case "item": return EntityScope.OfItem(Item, name, Now);
                    case "element": return EntityScope.OfElement(Element, name);
                    case "signal": return OfSignal(name);
                }
                return ExprValue.None;
            }

            // what the signal carries (a timeout: kind, action, waited; every signal: its time)
            private ExprValue OfSignal(string name)
            {
                switch (name)
                {
                    case "time": return ExprValue.Of(Signal.Time);
                    case "waited": return double.IsNaN(Signal.Waited) ? ExprValue.None : ExprValue.Of(Signal.Waited);
                    case "kind": return Signal.WaitKind != null ? ExprValue.Of(Signal.WaitKind) : ExprValue.None;
                    case "action": return Signal.WaitAction != null ? ExprValue.Of(Signal.WaitAction) : ExprValue.None;
                }
                return ExprValue.None;
            }
        }
    }

    /// <summary>A reaction's action failed (the run stops; the message names the reaction).</summary>
    public sealed class ReactionException : InvalidOperationException
    {
        public ReactionException(string message, Exception inner) : base(message, inner) { }
    }
}
