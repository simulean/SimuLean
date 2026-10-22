using System;
using System.Collections.Generic;

namespace SimuLean.Signals
{
    /// <summary>What happened at an element: the hook points elements already have.</summary>
    public enum SignalKind
    {
        /// <summary>An item entered the element (the link layer's entry, after the hand-over).</summary>
        ItemEntered,
        /// <summary>An item left the element.</summary>
        ItemExited,
        /// <summary>The element's visible state changed.</summary>
        StateChanged,
        /// <summary>A stop started on the element (downtime, a shift, a reaction).</summary>
        Stopped,
        /// <summary>A stop on the element ended.</summary>
        Resumed,
        /// <summary>A station finished processing an item (before sending it on).</summary>
        ProcessEnded,
        /// <summary>Time: once, when the run starts (no element).</summary>
        RunStarted,
        /// <summary>Time: every <c>period</c> (the first at <c>first</c>, default one period; no element).</summary>
        Every,
        /// <summary>Time: once, at time <c>at</c> (no element).</summary>
        AtTime,
    }

    /// <summary>What a signal kind carries.</summary>
    public static class SignalKinds
    {
        /// <summary>An item signal: the reaction sees the item (item labels allowed).</summary>
        public static bool HasItem(SignalKind kind) => kind == SignalKind.ItemEntered || kind == SignalKind.ItemExited || kind == SignalKind.ProcessEnded;
        /// <summary>A time signal: raised by the clock, from no element.</summary>
        public static bool IsTime(SignalKind kind) => kind == SignalKind.RunStarted || kind == SignalKind.Every || kind == SignalKind.AtTime;
        /// <summary>A signal with a state (the state filter applies).</summary>
        public static bool HasState(SignalKind kind) => kind == SignalKind.StateChanged || kind == SignalKind.Stopped || kind == SignalKind.Resumed;
    }

    /// <summary>When a signal reaches a subscriber.</summary>
    public enum SignalDelivery
    {
        /// <summary>A zero-delay event after the one that raised it (the default): deterministic, and the subscriber never
        /// runs in the middle of another element's step.</summary>
        NextEvent,
        /// <summary>At once, inside the step that raised it: sees the state before anything else moves (advanced).</summary>
        Immediate,
    }

    /// <summary>One signal: what, where, with which item / states, when. Passed by reference; nothing allocated per signal
    /// for an immediate subscriber.</summary>
    public readonly struct SignalArgs
    {
        public readonly SignalKind Kind;
        public readonly Element Source;
        /// <summary>The item (item signals), else null.</summary>
        public readonly Item Item;
        /// <summary>State changes: the former and the new state; stops: the stop's state (both); item signals: none
        /// (<c>default</c>).</summary>
        public readonly ElementState From, To;
        public readonly double Time;

        public SignalArgs(SignalKind kind, Element source, Item item, ElementState from, ElementState to, double time)
        {
            Kind = kind; Source = source; Item = item; From = from; To = to; Time = time;
        }
    }

    /// <summary>Something that reacts to signals (a <see cref="Reaction"/>, or code).</summary>
    public interface ISignalSink
    {
        void OnSignal(in SignalArgs signal);
    }

    /// <summary>
    /// The model's signal table (<c>SimContext.Signals</c>): subscribers register once, when the model is built,
    /// for a signal of an element; the table hooks that element's event only then, so a model without subscribers pays
    /// nothing. Firing walks a short list; a <see cref="SignalDelivery.NextEvent"/> subscriber gets the signal from a
    /// pooled zero-delay event.
    /// </summary>
    public sealed class SignalHub
    {
        private readonly SimClock clock;
        private readonly Dictionary<(Element, SignalKind), Channel> channels = new Dictionary<(Element, SignalKind), Channel>();
        private readonly Stack<Delivery> pool = new Stack<Delivery>();

        public SignalHub(SimClock clock) { this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); }

        /// <summary>Subscriptions in the table.</summary>
        public int Count { get; private set; }

        /// <summary>The stops reactions started, by element, still active, with their group: a resume action ends those of
        /// its group (whichever reaction stopped the element), never a downtime's.</summary>
        internal readonly Dictionary<Element, List<(StopToken token, string group)>> ReactionStops = new Dictionary<Element, List<(StopToken, string)>>();
        /// <summary>Team members reactions stopped, with the group, in order (a resume calls them back in that order).</summary>
        internal readonly List<(Element member, string group)> StoppedMembers = new List<(Element, string)>();

        private readonly List<Reaction> reactions = new List<Reaction>();
        /// <summary>The model's reactions (what the views list for an element).</summary>
        public IReadOnlyList<Reaction> Reactions => reactions;
        internal void Register(Reaction reaction) { if (!reactions.Contains(reaction)) reactions.Add(reaction); }

        /// <summary><paramref name="sink"/> gets <paramref name="kind"/> of <paramref name="publisher"/> from now on.</summary>
        public void Subscribe(Element publisher, SignalKind kind, ISignalSink sink, SignalDelivery delivery = SignalDelivery.NextEvent)
        {
            if (publisher == null) throw new ArgumentNullException(nameof(publisher));
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (!channels.TryGetValue((publisher, kind), out var channel))
            {
                channel = new Channel(this, publisher, kind);
                channels[(publisher, kind)] = channel;
            }
            channel.Add(sink, delivery);
            Count++;
        }

        /// <summary>Unhooks every subscription (a model built again on the same clock).</summary>
        public void Clear()
        {
            foreach (var c in channels.Values) c.Detach();
            channels.Clear();
            ReactionStops.Clear();
            StoppedMembers.Clear();
            reactions.Clear();
            Count = 0;
        }

        private void Deliver(ISignalSink sink, in SignalArgs args)
        {
            var d = pool.Count > 0 ? pool.Pop() : new Delivery(this);
            d.Sink = sink; d.Args = args;
            clock.ScheduleEvent(d, 0);
        }

        private sealed class Delivery : IEvent
        {
            private readonly SignalHub hub;
            public ISignalSink Sink;
            public SignalArgs Args;
            public Delivery(SignalHub hub) { this.hub = hub; }

            public void Execute()
            {
                var sink = Sink; var args = Args;
                Sink = null; Args = default;
                hub.pool.Push(this);
                sink.OnSignal(args);
            }
        }

        /// <summary>The subscribers of one signal of one element, hooked to its event once.</summary>
        private sealed class Channel
        {
            private readonly SignalHub hub;
            private readonly Element element;
            private readonly SignalKind kind;
            private readonly List<(ISignalSink sink, SignalDelivery delivery)> subscribers = new List<(ISignalSink, SignalDelivery)>(2);
            private readonly Action<Element, Item> onItem;
            private readonly Action<Element, ElementState, ElementState> onState;
            private readonly Action<Element, StopToken> onStop;

            public Channel(SignalHub hub, Element element, SignalKind kind)
            {
                this.hub = hub; this.element = element; this.kind = kind;
                switch (kind)
                {
                    case SignalKind.ItemEntered: onItem = (e, i) => Raise(i, default, default); element.ItemEntered += onItem; break;
                    case SignalKind.ItemExited: onItem = (e, i) => Raise(i, default, default); element.ItemExited += onItem; break;
                    case SignalKind.ProcessEnded: onItem = (e, i) => Raise(i, default, default); element.ProcessEnded += onItem; break;
                    default: throw new ArgumentException($"{kind} is not a signal of an element.");
                    case SignalKind.StateChanged: onState = (e, from, to) => Raise(null, from, to); element.StateChanged += onState; break;
                    case SignalKind.Stopped: onStop = (e, t) => Raise(null, t.State, t.State); element.Stopped += onStop; break;
                    case SignalKind.Resumed: onStop = (e, t) => Raise(null, t.State, t.State); element.Resumed += onStop; break;
                }
            }

            public void Add(ISignalSink sink, SignalDelivery delivery) => subscribers.Add((sink, delivery));

            public void Detach()
            {
                switch (kind)
                {
                    case SignalKind.ItemEntered: element.ItemEntered -= onItem; break;
                    case SignalKind.ItemExited: element.ItemExited -= onItem; break;
                    case SignalKind.ProcessEnded: element.ProcessEnded -= onItem; break;
                    case SignalKind.StateChanged: element.StateChanged -= onState; break;
                    case SignalKind.Stopped: element.Stopped -= onStop; break;
                    case SignalKind.Resumed: element.Resumed -= onStop; break;
                }
            }

            private void Raise(Item item, ElementState from, ElementState to)
            {
                var args = new SignalArgs(kind, element, item, from, to, hub.clock.GetSimulationTime());
                // In the order the subscribers registered (the reactions' build order): the immediate ones now, the
                // next-event ones as zero-delay events, which the clock runs in the order they were scheduled.
                for (int i = 0; i < subscribers.Count; i++)
                {
                    var (sink, delivery) = subscribers[i];
                    if (delivery == SignalDelivery.Immediate) sink.OnSignal(args);
                    else hub.Deliver(sink, args);
                }
            }
        }
    }
}
