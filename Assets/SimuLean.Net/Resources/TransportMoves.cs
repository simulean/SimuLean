using System;
using System.Collections.Generic;

namespace SimuLean.Resources
{
    /// <summary>
    /// The moves of the items of one element carried by resources (its <c>transportBy</c>): the request to the
    /// dispatcher, then the carrier's task — travel empty to the origin's location (spatial carriers), load (the origin
    /// hands the item over: <see cref="Element.SendHeldItem"/>, the link sees <see cref="TakeLoading"/> and fires
    /// <c>OnExit</c>), travel loaded to the destination, unload, deliver (the link's delivery: <c>Receive</c>,
    /// <c>OnEntry</c>); if the destination cannot take it then, the carrier waits, HELD, until
    /// <see cref="DeliverWaiting"/>. Shared by the direct transport link (<see cref="ResourceTransportLink"/>) and the
    /// list link (the holder's transport carries the items the list hands to a puller).
    /// </summary>
    public sealed class TransportMoves
    {
        private readonly Element origin;
        private readonly List<Arrival> waiting = new List<Arrival>();
        private Item loadingNow;
        private readonly List<Action<Element, StopToken>> waitingForResume = new List<Action<Element, StopToken>>();

        // One move: its state, read by the carrier's task steps through these methods (no closures).
        // The move's times are read when the step runs.
        private sealed class Arrival : ITaskInterruption
        {
            public TransportMoves Moves; public Item Item; public Element Destination; public TaskExecutor Executor; public bool Delivered;
            public bool WaitingForOrigin;   // loaded while the origin was stopped: the hand-over waits for its resume
            public Func<Item, Element, bool> Deliver;

            public void OnAssigned(ResourceRequest r, IReadOnlyList<TaskExecutor> got) => Moves.Start(this, r, got);
            public void LeadIs(TaskExecutor lead) => Executor = lead;
            public void Counted() => Moves.Interruptions++;
            public double LoadTime() => Moves.LoadTime?.Invoke(Item) ?? Executor.LoadTime;
            public void OnLoaded(Item it) => Moves.Loaded(it, this);
            public bool OriginReady() => !WaitingForOrigin;
            public double TravelTime() => Moves.TravelTime(Item, Destination);
            public double UnloadTime() => Moves.UnloadTime?.Invoke(Item) ?? Executor.UnloadTime;
            public void TryDeliver() => Moves.TryDeliver(this);
            public bool IsDelivered() => Delivered;

            // a carrier on its way, empty, to the pick-up (a move of one carrier, not loaded yet; a timed or an
            // open-ended move) can be taken; the move asks for a carrier again. Loaded, it delivers. A move of several
            // carriers is a CarrierGroup.
            public bool Alone;
            public bool CanInterrupt(TaskSequence task, TaskStep step, PreemptMode mode)
                => mode != PreemptMode.Abort && Alone && step.Kind == StepKind.Travel && task.CurrentStep == 0;
            public bool InterruptsOnStop(TaskSequence task, TaskStep step) => false;   // a stop pauses its travel
            public void Interrupted(TaskSequence task, TaskExecutor executor, PreemptMode mode, bool byStop)
            {
                Executor = null;
                Moves.Interruptions++;
                Moves.Ask(this);
            }
            public void Paused(TaskSequence task, TaskExecutor executor, bool paused) { }
        }

        /// <summary>Carriers taken from this element's moves (on their way, empty, to the pick-up, or waiting there for
        /// the rest of their group) by requests of higher priority or by their stop.</summary>
        public int Interruptions { get; private set; }

        public TransportMoves(Element origin, StationResources transport)
        {
            this.origin = origin ?? throw new ArgumentNullException(nameof(origin));
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>Dispatcher, requirements and priority of the transport requests.</summary>
        public StationResources Transport { get; }
        public Element Origin => origin;

        /// <summary>Travel time of a move (item, destination); null = 0 for abstract carriers and the carrier's travel
        /// model for spatial ones (set, it overrides the model's time; the carrier still ends at the destination).</summary>
        public Func<Item, Element, double> TravelTime { get; set; }
        /// <summary>Load time per item; null = the resource's own <see cref="TaskExecutor.LoadTime"/>.</summary>
        public Func<Item, double> LoadTime { get; set; }
        /// <summary>Unload time per item; null = the resource's own <see cref="TaskExecutor.UnloadTime"/>.</summary>
        public Func<Item, double> UnloadTime { get; set; }

        /// <summary>Called when the carrier has loaded the item (before the origin hands it over).</summary>
        public Action<Item> Loading { get; set; }

        public void Reset()
        {
            waiting.Clear(); loadingNow = null;
            foreach (var h in waitingForResume) origin.Resumed -= h;   // carriers waiting for a stopped origin: a new run
            waitingForResume.Clear();
        }

        /// <summary>Requests a carrier for <paramref name="item"/> to <paramref name="dest"/>; <paramref name="deliver"/>
        /// puts it into the destination (false = it cannot take it now: the carrier waits).</summary>
        public void Request(Item item, Element dest, Func<Item, Element, bool> deliver)
            => Ask(new Arrival { Moves = this, Item = item, Destination = dest, Deliver = deliver });

        private void Ask(Arrival arrival)
        {
            var request = new ResourceRequest(origin, TaskType.Transport, arrival.OnAssigned, Transport.Requirements);
            request.Priority = Transport.Priority?.Invoke(arrival.Item) ?? 1;
            request.Preempt = Transport.Preempt;
            request.Location = origin.Location;              // "nearest vehicle" rules measure to the pick-up
            Transport.Dispatcher.Request(request);
        }

        /// <summary>True (once) when <paramref name="item"/> is the item being handed over to its carrier: the link lets
        /// it leave the origin.</summary>
        public bool TakeLoading(Item item)
        {
            if (item == null || item != loadingNow) return false;
            loadingNow = null;
            return true;
        }

        /// <summary>A destination has room: the first carrier waiting there delivers. Null = none waits there; false = it
        /// still cannot take the item.</summary>
        public bool? DeliverWaiting(Element dest)
        {
            for (int i = 0; i < waiting.Count; i++)
            {
                var a = waiting[i];
                if (a.Destination != dest) continue;
                // Out of the queue before delivering: the delivery can notify the destination's input again, which
                // re-enters here and delivers other waiting carriers; the queue then changes under this index.
                waiting.RemoveAt(i);
                if (!a.Deliver(a.Item, a.Destination))
                {
                    waiting.Insert(Math.Min(i, waiting.Count), a);
                    return false;
                }
                a.Delivered = true;
                a.Executor.Continue();
                return true;
            }
            return null;
        }

        // ── the move ─────────────────────────────────────────────────────────

        private void Start(Arrival arrival, ResourceRequest request, IReadOnlyList<TaskExecutor> got)
        {
            if (got.Count > 1) { StartGroup(arrival, request, got); return; }
            var carrier = got[0];
            arrival.Executor = carrier;
            arrival.Alone = got.Count == 1;
            Item item = arrival.Item; Element dest = arrival.Destination;
            var lead = new TaskSequence(TaskType.Transport, origin, "Transport " + item.GetId())
            {
                Priority = Transport.Priority?.Invoke(item) ?? 1, Preemptable = Transport.Preemptable,
            };
            lead.Interruption = arrival;
            bool spatial = carrier.Mode == ResourceMode.Spatial;
            if (spatial)
            {
                if (origin.Location == null || dest.Location == null)
                    throw new InvalidOperationException($"Transport by '{carrier.Name}' from '{origin.Name}' to '{dest.Name}': " +
                                                        "both elements need a location (network node) for a spatial carrier.");
                lead.Travel(origin.Location);                // empty, to the pick-up
            }
            lead.Load(item, arrival.LoadTime, arrival.OnLoaded)
                .HoldUntil(arrival.OriginReady);   // no hold unless the origin is stopped
            if (TravelTime != null) lead.Travel(arrival.TravelTime, spatial ? dest.Location : null);
            else if (spatial) lead.Travel(dest.Location);
            lead.Unload(item, arrival.UnloadTime)
                .Callback(arrival.TryDeliver)
                .HoldUntil(arrival.IsDelivered);
            got[0].Execute(lead);
        }

        // Several carriers move the item together (CarrierGroup): they gather at the pick-up, then the lead loads, carries
        // and unloads it, the others with it.
        private void StartGroup(Arrival arrival, ResourceRequest request, IReadOnlyList<TaskExecutor> got)
        {
            Item item = arrival.Item; Element dest = arrival.Destination;
            foreach (var c in got)
                if (c.Mode == ResourceMode.Spatial && (origin.Location == null || dest.Location == null))
                    throw new InvalidOperationException($"Transport by '{c.Name}' from '{origin.Name}' to '{dest.Name}': " +
                                                        "both elements need a location (network node) for a spatial carrier.");
            arrival.Alone = false;
            var group = new CarrierGroup(origin, request, Transport.Dispatcher, Transport.Preemptable, origin.Location,
                                         "Transport " + item.GetId(), "Help " + item.GetId(), (lead, carrier) =>
                                         {
                                             bool spatial = carrier.Mode == ResourceMode.Spatial;
                                             lead.Load(item, arrival.LoadTime, arrival.OnLoaded).HoldUntil(arrival.OriginReady);
                                             if (TravelTime != null) lead.Travel(arrival.TravelTime, spatial ? dest.Location : null);
                                             else if (spatial) lead.Travel(dest.Location);
                                             lead.Unload(item, arrival.UnloadTime).Callback(arrival.TryDeliver).HoldUntil(arrival.IsDelivered);
                                         }, arrival.LeadIs, arrival.Counted);
            group.Start(got);
        }

        private void Loaded(Item item, Arrival arrival)
        {
            Loading?.Invoke(item);
            if (origin.IsOutputBlocked) { WaitForOrigin(item, arrival); return; }
            HandOverOrRetry(item);
        }

        // A stopped origin keeps its items (downtime blocking is not bypassed): the carrier waits, HELD,
        // and takes the item when the stop ends.
        // In its own method: a closure that captures a parameter is allocated at every call, used or not.
        private void WaitForOrigin(Item item, Arrival arrival)
        {
            arrival.WaitingForOrigin = true;
            Action<Element, StopToken> onResumed = null;
            onResumed = (e, token) =>
            {
                if (origin.IsOutputBlocked) return;
                origin.Resumed -= onResumed;
                waitingForResume.Remove(onResumed);
                arrival.WaitingForOrigin = false;
                HandOverOrRetry(item);
                arrival.Executor.Continue();
            };
            origin.Resumed += onResumed;
            waitingForResume.Add(onResumed);
        }

        private void HandOverOrRetry(Item item)
        {
            if (HandOver(item)) return;
            HandOverLater(item);
        }

        // Loaded inside the origin's own send (a free resource, no load time): the origin has not finished its bookkeeping
        // yet — hand the item over after the current event, at the same time.
        // In its own method: a closure that captures a parameter is allocated at every call, used or not.
        private void HandOverLater(Item item)
        {
            origin.GetSimClock().Schedule(() =>
            {
                if (!HandOver(item))
                    throw new InvalidOperationException($"'{origin.Name}' did not hand item {item.GetId()} over to its transporter.");
            }, 0);
        }

        private bool HandOver(Item item)
        {
            loadingNow = item;
            if (!origin.SendHeldItem(item) && loadingNow == item) origin.NotifyUnblock();
            bool done = loadingNow != item;
            loadingNow = null;
            return done;
        }

        private void TryDeliver(Arrival a)
        {
            if (a.Deliver(a.Item, a.Destination)) { a.Delivered = true; return; }
            waiting.Add(a);                                   // waits, HELD, until the destination has room
        }
    }
}
