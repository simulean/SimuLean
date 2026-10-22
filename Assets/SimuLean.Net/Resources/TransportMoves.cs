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

        private sealed class Arrival
        {
            public Item Item; public Element Destination; public TaskExecutor Executor; public bool Delivered;
            public bool WaitingForOrigin;   // loaded while the origin was stopped: the hand-over waits for its resume
            public Func<Item, Element, bool> Deliver;
        }

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
        {
            var request = new ResourceRequest(origin, TaskType.Transport, (r, got) => Start(item, dest, got, deliver),
                                              new List<ResourceRequirement>(Transport.Requirements).ToArray());
            request.Priority = Transport.Priority?.Invoke(item) ?? 0;
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
                if (!a.Deliver(a.Item, a.Destination)) return false;
                a.Delivered = true;
                waiting.RemoveAt(i);
                a.Executor.Continue();
                return true;
            }
            return null;
        }

        // ── the move ─────────────────────────────────────────────────────────

        private void Start(Item item, Element dest, IReadOnlyList<TaskExecutor> got, Func<Item, Element, bool> deliver)
        {
            var carrier = got[0];
            var arrival = new Arrival { Item = item, Destination = dest, Executor = carrier, Deliver = deliver };
            var lead = new TaskSequence(TaskType.Transport, origin, "Transport " + item.GetId());
            bool spatial = carrier.Mode == ResourceMode.Spatial;
            if (spatial)
            {
                if (origin.Location == null || dest.Location == null)
                    throw new InvalidOperationException($"Transport by '{carrier.GetName()}' from '{origin.GetName()}' to '{dest.GetName()}': " +
                                                        "both elements need a location (network node) for a spatial carrier.");
                lead.Travel(origin.Location);                // empty, to the pick-up
            }
            lead.Load(item, () => LoadTime?.Invoke(item) ?? carrier.LoadTime, it => Loaded(it, arrival))
                .HoldUntil(() => !arrival.WaitingForOrigin);   // no hold unless the origin is stopped
            if (TravelTime != null) lead.Travel(() => TravelTime(item, dest), spatial ? dest.Location : null);
            else if (spatial) lead.Travel(dest.Location);
            lead.Unload(item, () => UnloadTime?.Invoke(item) ?? carrier.UnloadTime)
                .Callback(() => TryDeliver(arrival))
                .HoldUntil(() => arrival.Delivered);
            // Helpers (requests needing several resources) accompany the lead and are freed with it.
            for (int k = 1; k < got.Count; k++)
            {
                var helper = got[k];
                helper.Execute(new TaskSequence(TaskType.Transport, origin, "Help " + item.GetId()).Hold(ResourceStates.TRAVELLING_LOADED));
                lead.Completed += _ => helper.Continue();
            }
            got[0].Execute(lead);
        }

        private void Loaded(Item item, Arrival arrival)
        {
            Loading?.Invoke(item);
            if (origin.IsOutputBlocked)
            {
                // A stopped origin keeps its items (downtime blocking is not bypassed): the carrier
                // waits, HELD, and takes the item when the stop ends.
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
                return;
            }
            HandOverOrRetry(item);
        }

        private void HandOverOrRetry(Item item)
        {
            if (HandOver(item)) return;
            // Loaded inside the origin's own send (a free resource, no load time): the origin has not finished
            // its bookkeeping yet — hand the item over after the current event, at the same time.
            origin.GetSimClock().Schedule(() =>
            {
                if (!HandOver(item))
                    throw new InvalidOperationException($"'{origin.GetName()}' did not hand item {item.GetId()} over to its transporter.");
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
